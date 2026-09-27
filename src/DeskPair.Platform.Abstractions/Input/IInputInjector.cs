namespace DeskPair.Platform.Abstractions.Input;

/// <summary>
/// Which of an injector's calls do what they say on this platform.
///
/// The interface has always had more on it than every platform can honour, and the ones that cannot have
/// answered with silence: X11 returns no lock-key state because it never asks for any, and its
/// LockWorkstation is an empty method, so the viewer's "lock the remote machine" button has been doing
/// nothing on Linux without saying so. A default is not an answer to a question about the machine -- a
/// viewer that is told Caps Lock is off shows a Caps Lock indicator that is off, whatever the keyboard is
/// actually doing.
///
/// Stated here so the answer can be "this platform cannot" rather than a plausible wrong value. It is
/// added while Linux has one injector; it will shortly have three -- X11, the Wayland portal and uinput --
/// and they differ from each other on exactly these lines.
/// </summary>
[Flags]
public enum InputCapabilities
{
    None = 0,

    /// <summary>Pointer movement, buttons and the wheel.</summary>
    Mouse = 1 << 0,

    /// <summary>Key presses and typed text.</summary>
    Keyboard = 1 << 1,

    /// <summary>
    /// <see cref="IInputInjector.GetLockKeyStates"/> answers about this machine, and
    /// <see cref="IInputInjector.SetLockKeyStates"/> changes it. Without this the viewer must show nothing
    /// rather than a default.
    /// </summary>
    LockKeys = 1 << 2,

    /// <summary>
    /// <see cref="IInputInjector.SendCtrlAltDel"/> reaches the operating system's own attention sequence --
    /// the one an application cannot intercept. Sending the three keys and hoping something is bound to
    /// them is not this.
    /// </summary>
    SecureAttention = 1 << 3,

    /// <summary><see cref="IInputInjector.LockWorkstation"/> locks the screen.</summary>
    LockScreen = 1 << 4,

    All = Mouse | Keyboard | LockKeys | SecureAttention | LockScreen,
}

public interface IInputInjector : IAsyncDisposable
{
    /// <summary>
    /// What this injector can actually do; see <see cref="InputCapabilities"/>.
    ///
    /// Defaulted to everything so an implementation that has not been asked the question keeps compiling,
    /// and so the cost of the honest answer falls on whoever knows it.
    /// </summary>
    InputCapabilities Capabilities => InputCapabilities.All;

    /// <summary>
    /// Attaches the calling thread to the current input desktop. On Windows this must run before every
    /// injection so the secure desktop (UAC, logon) receives input; other platforms no-op.
    /// </summary>
    void EnsureInputDesktop();

    void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen);

    void InjectKey(in KeyInput input);

    LockKeyStates GetLockKeyStates();

    void SetLockKeyStates(LockKeyStates states);

    /// <summary>Releases every key and button this injector pressed; called on disconnect.</summary>
    void ReleaseAll();

    void SendCtrlAltDel();

    void LockWorkstation();
}
