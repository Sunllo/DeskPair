using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Input;

/// <summary>
/// SendInput-based injector. Mouse positions are mapped onto the virtual desktop (all monitors);
/// keys go in as scancodes (Map mode) or Unicode characters (Translate mode). Every injected event carries
/// a tag in dwExtraInfo so local hooks can tell remote input from the user's own.
/// </summary>
public sealed class WindowsInputInjector : IInputInjector
{
    private readonly ILogger _log;
    private readonly HashSet<ushort> _downScancodes = new();
    private readonly HashSet<ushort> _downVirtualKeys = new();
    private readonly object _lock = new();
    private MouseButtons _downButtons;
    private readonly DesktopBoundThread _thread;

    public WindowsInputInjector(ILogger log)
    {
        _log = log;
        _thread = new DesktopBoundThread(log);
    }

    /// <summary>
    /// Nothing to do: the thread that injects attaches itself to the input desktop before every batch.
    ///
    /// This used to call SetThreadDesktop here, on whichever thread the message had arrived on -- a
    /// thread-pool thread, so the next keystroke might run on a different one that was still attached to
    /// Default, and the ones that had been switched carried an attachment to the Winlogon desktop into
    /// whatever the pool gave them next. The binding belongs to a thread, so it now belongs to a thread
    /// that does nothing else.
    /// </summary>
    public void EnsureInputDesktop()
    {
    }

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen)
    {
        // Copied out of the in-parameters before the lambda: a by-reference argument cannot be captured,
        // and by the time the injecting thread reads it the caller's frame is gone anyway.
        MouseInput mouse = input;
        VirtualScreenRect screen = virtualScreen;
        _thread.Post(() => InjectMouseCore(mouse, screen));
    }

    private void InjectMouseCore(MouseInput input, VirtualScreenRect virtualScreen)
    {
        var inputs = new List<User32.INPUT>(2);
        switch (input.Action)
        {
            case MouseAction.Move:
                inputs.Add(Mouse(User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE | User32.MOUSEEVENTF_VIRTUALDESK, ToAbsolute(input.X, virtualScreen.X, virtualScreen.Width), ToAbsolute(input.Y, virtualScreen.Y, virtualScreen.Height)));
                break;
            case MouseAction.MoveRelative:
                inputs.Add(Mouse(User32.MOUSEEVENTF_MOVE, input.X, input.Y));
                break;
            case MouseAction.Down:
            case MouseAction.Up:
                bool down = input.Action == MouseAction.Down;
                // Position first so the click lands where the controller thinks the cursor is.
                inputs.Add(Mouse(User32.MOUSEEVENTF_MOVE | User32.MOUSEEVENTF_ABSOLUTE | User32.MOUSEEVENTF_VIRTUALDESK, ToAbsolute(input.X, virtualScreen.X, virtualScreen.Width), ToAbsolute(input.Y, virtualScreen.Y, virtualScreen.Height)));
                foreach ((MouseButtons button, uint downFlag, uint upFlag, uint data) in ButtonFlags)
                {
                    if (input.Buttons.HasFlag(button))
                    {
                        inputs.Add(Mouse(down ? downFlag : upFlag, 0, 0, data));
                        lock (_lock)
                        {
                            if (down)
                            {
                                _downButtons |= button;
                            }
                            else
                            {
                                _downButtons &= ~button;
                            }
                        }
                    }
                }

                break;
            case MouseAction.Wheel:
                inputs.Add(Mouse(User32.MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)(input.WheelDelta * User32.WHEEL_DELTA))));
                break;
            case MouseAction.HorizontalWheel:
                inputs.Add(Mouse(User32.MOUSEEVENTF_HWHEEL, 0, 0, unchecked((uint)(input.WheelDelta * User32.WHEEL_DELTA))));
                break;
        }

        Send(inputs);
    }

    public void InjectKey(in KeyInput input)
    {
        KeyInput key = input;
        _thread.Post(() => InjectKeyCore(key));
    }

    private void InjectKeyCore(KeyInput input)
    {
        if (input.Control != ControlKey.None)
        {
            ushort vk = VirtualKeys.FromControlKey(input.Control);
            if (vk == 0)
            {
                return;
            }

            SendVirtualKey(vk, input.Down);
            return;
        }

        if (input.Mode == KeyInputMode.Map && input.Code != 0)
        {
            // Code is a Windows scancode (possibly with 0xE0 prefix in the high byte) from the controller.
            ushort scan = (ushort)(input.Code & 0xFF);
            bool extended = (input.Code & 0xE000) == 0xE000 || (input.Code >> 8) == 0xE0;
            SendScancode(scan, extended, input.Down);
            return;
        }

        if (!string.IsNullOrEmpty(input.Text))
        {
            foreach (char c in input.Text)
            {
                SendUnicode(c, true);
                SendUnicode(c, false);
            }

            return;
        }

        if (input.Code != 0)
        {
            SendVirtualKey((ushort)input.Code, input.Down);
        }
    }

    /// <summary>
    /// All five, and stated rather than left to the default, so that the default only ever means "nobody
    /// has answered this yet". Windows is the platform the interface was drawn from, which is why it is
    /// also the only one that honours every line of it.
    /// </summary>
    public InputCapabilities Capabilities => InputCapabilities.All;

    /// <summary>
    /// GetKeyState answers about the calling thread's desktop, not about the keyboard, so this is asked
    /// on the thread that is attached to the right one.
    /// </summary>
    public LockKeyStates GetLockKeyStates() => _thread.Call(LockKeyStatesCore);

    private static LockKeyStates LockKeyStatesCore() => new(
        (User32.GetKeyState(0x14) & 1) != 0,
        (User32.GetKeyState(0x90) & 1) != 0,
        (User32.GetKeyState(0x91) & 1) != 0);

    public void SetLockKeyStates(LockKeyStates states) => _thread.Post(() => SetLockKeyStatesCore(states));

    private void SetLockKeyStatesCore(LockKeyStates states)
    {
        // The core reader, not the public one: this is already running on the injecting thread, and
        // asking it to run something and wait from inside itself would wait for ever.
        LockKeyStates current = LockKeyStatesCore();
        if (current.CapsLock != states.CapsLock)
        {
            Tap(0x14);
        }

        if (current.NumLock != states.NumLock)
        {
            Tap(0x90);
        }

        if (current.ScrollLock != states.ScrollLock)
        {
            Tap(0x91);
        }
    }

    public void ReleaseAll() => _thread.Post(ReleaseAllCore);

    private void ReleaseAllCore()
    {
        ushort[] scans;
        ushort[] vks;
        MouseButtons buttons;
        lock (_lock)
        {
            scans = _downScancodes.ToArray();
            vks = _downVirtualKeys.ToArray();
            buttons = _downButtons;
            _downScancodes.Clear();
            _downVirtualKeys.Clear();
            _downButtons = MouseButtons.None;
        }

        foreach (ushort s in scans)
        {
            SendScancode((ushort)(s & 0xFF), (s & 0x100) != 0, false);
        }

        foreach (ushort vk in vks)
        {
            SendVirtualKey(vk, false);
        }

        var inputs = new List<User32.INPUT>();
        foreach ((MouseButtons button, _, uint upFlag, uint data) in ButtonFlags)
        {
            if (buttons.HasFlag(button))
            {
                inputs.Add(Mouse(upFlag, 0, 0, data));
            }
        }

        Send(inputs);
    }

    public void SendCtrlAltDel() => _thread.Post(SendCtrlAltDelCore);

    private void SendCtrlAltDelCore()
    {
        // Only SYSTEM can generate the secure attention sequence through sas.dll, and DeskPair runs as the
        // signed-in user now that there is no service, so this call fails and is logged. It stays because
        // the policy "SoftwareSASGeneration" lets an administrator permit it, and because failing here is
        // better than pretending the key was sent.
        try
        {
            SasNative.SendSAS(0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.LogWarning(e, "SendSAS is unavailable in this process");
        }
    }

    public void LockWorkstation() => _thread.Post(() => User32.LockWorkStation());

    private void SendScancode(ushort scan, bool extended, bool down)
    {
        uint flags = User32.KEYEVENTF_SCANCODE | (extended ? User32.KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : User32.KEYEVENTF_KEYUP);
        Send([Key(0, scan, flags)]);
        lock (_lock)
        {
            ushort key = (ushort)(scan | (extended ? 0x100 : 0));
            if (down)
            {
                _downScancodes.Add(key);
            }
            else
            {
                _downScancodes.Remove(key);
            }
        }
    }

    private void SendVirtualKey(ushort vk, bool down)
    {
        nint layout = User32.GetKeyboardLayout(User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), 0));
        ushort scan = (ushort)User32.MapVirtualKeyExW(vk, User32.MAPVK_VK_TO_VSC_EX, layout);
        bool extended = (scan >> 8) is 0xE0 or 0xE1 || VirtualKeys.IsExtended(vk);
        uint flags = (extended ? User32.KEYEVENTF_EXTENDEDKEY : 0) | (down ? 0 : User32.KEYEVENTF_KEYUP);
        Send([Key(vk, (ushort)(scan & 0xFF), flags)]);
        lock (_lock)
        {
            if (down)
            {
                _downVirtualKeys.Add(vk);
            }
            else
            {
                _downVirtualKeys.Remove(vk);
            }
        }
    }

    private void SendUnicode(char c, bool down) =>
        Send([Key(0, c, User32.KEYEVENTF_UNICODE | (down ? 0 : User32.KEYEVENTF_KEYUP))]);

    private void Tap(ushort vk)
    {
        SendVirtualKey(vk, true);
        SendVirtualKey(vk, false);
    }

    private void Send(List<User32.INPUT> inputs)
    {
        if (inputs.Count == 0)
        {
            return;
        }

        User32.INPUT[] array = inputs.ToArray();
        uint sent = User32.SendInput((uint)array.Length, array, Marshal.SizeOf<User32.INPUT>());
        if (sent != array.Length)
        {
            _log.LogDebug("SendInput injected {Sent}/{Total} events (error {Error})", sent, array.Length, Marshal.GetLastWin32Error());
        }
    }

    private static int ToAbsolute(int value, int origin, int extent) =>
        extent <= 1 ? 0 : (int)Math.Clamp((long)(value - origin) * 65535 / (extent - 1), 0, 65535);

    private static User32.INPUT Mouse(uint flags, int dx, int dy, uint data = 0) => new()
    {
        type = User32.INPUT_MOUSE,
        u = new User32.INPUTUNION { mi = new User32.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = User32.InjectedTag } },
    };

    private static User32.INPUT Key(ushort vk, ushort scan, uint flags) => new()
    {
        type = User32.INPUT_KEYBOARD,
        u = new User32.INPUTUNION { ki = new User32.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = User32.InjectedTag } },
    };

    private static readonly (MouseButtons Button, uint Down, uint Up, uint Data)[] ButtonFlags =
    [
        (MouseButtons.Left, User32.MOUSEEVENTF_LEFTDOWN, User32.MOUSEEVENTF_LEFTUP, 0),
        (MouseButtons.Right, User32.MOUSEEVENTF_RIGHTDOWN, User32.MOUSEEVENTF_RIGHTUP, 0),
        (MouseButtons.Middle, User32.MOUSEEVENTF_MIDDLEDOWN, User32.MOUSEEVENTF_MIDDLEUP, 0),
        (MouseButtons.Back, User32.MOUSEEVENTF_XDOWN, User32.MOUSEEVENTF_XUP, User32.XBUTTON1),
        (MouseButtons.Forward, User32.MOUSEEVENTF_XDOWN, User32.MOUSEEVENTF_XUP, User32.XBUTTON2),
    ];

    public ValueTask DisposeAsync()
    {
        // Queued, then drained by Dispose below: a session that ends with keys held down must not leave
        // them held down on the desk.
        ReleaseAll();
        _thread.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal static partial class SasNative
{
    [LibraryImport("sas.dll")]
    public static partial void SendSAS([MarshalAs(UnmanagedType.Bool)] bool asUser);

    public static void SendSAS(int asUser) => SendSAS(asUser != 0);
}
