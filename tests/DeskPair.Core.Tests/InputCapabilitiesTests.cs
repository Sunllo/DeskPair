using DeskPair.Platform.Abstractions.Input;

namespace DeskPair.Core.Tests;

/// <summary>
/// What an injector says it can do, and what it means to say nothing.
///
/// The default is everything, deliberately: adding the question to the interface must not turn every
/// existing implementation into one that claims nothing and has its input ignored. The cost is that
/// silence and "yes to all five" are the same answer, which is why the platforms that do know have been
/// made to say so explicitly -- Windows included, so the default is only ever reached by an implementation
/// nobody has asked yet.
/// </summary>
public class InputCapabilitiesTests
{
    /// <summary>An injector that has not been asked claims everything, and keeps working.</summary>
    [Fact]
    public void Silence_means_everything()
    {
        IInputInjector quiet = new Unanswered();

        quiet.Capabilities.ShouldBe(InputCapabilities.All);
    }

    /// <summary>
    /// The one an X11 host gives: a pointer and a keyboard, no lock-key readings, no secure attention
    /// sequence, and no screen lock. A consumer asks with HasFlag, so the answer has to compose.
    /// </summary>
    [Fact]
    public void A_partial_answer_is_readable_one_line_at_a_time()
    {
        InputCapabilities x11 = InputCapabilities.Mouse | InputCapabilities.Keyboard;

        x11.HasFlag(InputCapabilities.Keyboard).ShouldBeTrue();
        x11.HasFlag(InputCapabilities.LockKeys).ShouldBeFalse();
        x11.HasFlag(InputCapabilities.LockScreen).ShouldBeFalse();
        x11.HasFlag(InputCapabilities.SecureAttention).ShouldBeFalse();
    }

    /// <summary>Every line of the interface the flags stand for is one of them, so All really is all.</summary>
    [Fact]
    public void All_covers_each_of_them()
    {
        foreach (InputCapabilities one in Enum.GetValues<InputCapabilities>())
        {
            InputCapabilities.All.HasFlag(one).ShouldBeTrue($"{one} must be part of All");
        }
    }

    private sealed class Unanswered : IInputInjector
    {
        public void EnsureInputDesktop() { }

        public void InjectMouse(in MouseInput input, in VirtualScreenRect virtualScreen) { }

        public void InjectKey(in KeyInput input) { }

        public LockKeyStates GetLockKeyStates() => default;

        public void SetLockKeyStates(LockKeyStates states) { }

        public void ReleaseAll() { }

        public void SendCtrlAltDel() { }

        public void LockWorkstation() { }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
