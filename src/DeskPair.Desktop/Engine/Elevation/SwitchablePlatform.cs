using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Abstractions.Input;
using DeskPair.Protocol.Helper;

namespace DeskPair.Desktop.Engine.Elevation;

/// <summary>
/// The three platform pieces the engine reads and drives the desktop through -- capture, input and cursor --
/// each wrapped so it can be switched from the local implementation to a raised SYSTEM helper and back. Only the
/// capturer swap needs a stream restart (a new capturer is built from the helper); input and cursor redirect at
/// once. The engine installs these only in app mode; the SYSTEM service reads the secure desktop itself.
/// </summary>
internal sealed class SwitchableScreenCapturerFactory(IScreenCapturerFactory local) : IScreenCapturerFactory
{
    private volatile HelperClientLink? _helper;
    private int _nextId;

    public void Attach(HelperClientLink helper) => _helper = helper;

    public void Detach() => _helper = null;

    public IScreenCapturer Create(DisplayDescriptor display, bool preferGpu)
    {
        if (_helper is { } helper)
        {
            int id = Interlocked.Increment(ref _nextId);
            var capturer = new HelperScreenCapturer(id, display)
            {
                OnDispose = () =>
                {
                    helper.Unregister(id);
                    helper.Post(new HelperMessage { Dispose = new DisposeCapturer { Id = id } });
                },
            };
            helper.Register(capturer);
            helper.Post(new HelperMessage { Create = new CreateCapturer { Id = id, Display = display.Index, PreferGpu = preferGpu } });
            return capturer;
        }

        return local.Create(display, preferGpu);
    }
}

internal sealed class SwitchableInputInjector(IInputInjector local) : IInputInjector
{
    private volatile HelperClientLink? _helper;

    public void Attach(HelperClientLink helper) => _helper = helper;

    public void Detach() => _helper = null;

    public InputCapabilities Capabilities => local.Capabilities;

    public void EnsureInputDesktop()
    {
        if (_helper is null)
        {
            local.EnsureInputDesktop(); // the helper attaches to the input desktop on its own side
        }
    }

    public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen)
    {
        if (_helper is { } helper)
        {
            helper.Post(new HelperMessage
            {
                Mouse = new HelperMouse
                {
                    Action = (int)input.Action,
                    Buttons = (int)input.Buttons,
                    X = input.X,
                    Y = input.Y,
                    Wheel = input.WheelDelta,
                    VsX = virtualScreen.X,
                    VsY = virtualScreen.Y,
                    VsW = virtualScreen.Width,
                    VsH = virtualScreen.Height,
                },
            });
        }
        else
        {
            local.InjectMouse(input, virtualScreen);
        }
    }

    public void InjectKey(in KeyInput input)
    {
        if (_helper is { } helper)
        {
            helper.Post(new HelperMessage
            {
                Key = new HelperKey
                {
                    Mode = (int)input.Mode,
                    Down = input.Down,
                    Code = input.Code,
                    Control = (int)input.Control,
                    Text = input.Text ?? string.Empty,
                },
            });
        }
        else
        {
            local.InjectKey(input);
        }
    }

    public LockKeyStates GetLockKeyStates() => local.GetLockKeyStates();

    public void SetLockKeyStates(LockKeyStates states)
    {
        if (_helper is { } helper)
        {
            helper.Post(new HelperMessage { LockKeys = new HelperLockKeys { CapsLock = states.CapsLock, NumLock = states.NumLock, ScrollLock = states.ScrollLock } });
        }
        else
        {
            local.SetLockKeyStates(states);
        }
    }

    public void ReleaseAll()
    {
        _helper?.Post(new HelperMessage { ReleaseAll = true });
        local.ReleaseAll();
    }

    // The secure attention sequence and locking are the local machine's own; the helper does not do them.
    public void SendCtrlAltDel() => local.SendCtrlAltDel();

    public void LockWorkstation() => local.LockWorkstation();

    public ValueTask DisposeAsync() => local.DisposeAsync();
}

internal sealed class SwitchableCursorProvider(ICursorProvider local) : ICursorProvider
{
    private volatile HelperClientLink? _helper;

    public void Attach(HelperClientLink helper) => _helper = helper;

    public void Detach() => _helper = null;

    public ulong GetCurrentCursorId() => _helper is { } helper ? helper.CursorId : local.GetCurrentCursorId();

    public CursorImage? GetCursorImage(ulong id)
    {
        if (_helper is { } helper)
        {
            return helper.CursorShape(id) is { } shape
                ? new CursorImage(shape.Id, shape.HotX, shape.HotY, shape.Width, shape.Height, shape.Bgra.ToByteArray())
                : null;
        }

        return local.GetCursorImage(id);
    }

    public (int X, int Y)? GetCursorPosition() => _helper is { } helper ? helper.CursorPosition : local.GetCursorPosition();

    public ValueTask DisposeAsync() => local.DisposeAsync();
}
