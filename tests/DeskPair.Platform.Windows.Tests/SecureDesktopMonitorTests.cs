using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Capture;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// The Windows secure-desktop monitor answers by whether it can open the desktop taking input. The live answer
/// depends on what the machine is showing, so the only thing safe to assert everywhere is that it returns a
/// defined state and never throws; where the interactive desktop is readable, that state is <c>None</c>.
/// </summary>
public class SecureDesktopMonitorTests
{
    [Fact]
    public void Poll_answers_a_defined_state_and_never_throws()
    {
        var monitor = new WindowsSecureDesktopMonitor(NullLogger.Instance);

        SecureDesktopState first = monitor.Poll();
        SecureDesktopState again = monitor.Poll();

        Enum.IsDefined(first.Kind).ShouldBeTrue();
        Enum.IsDefined(again.Kind).ShouldBeTrue();
    }

    [Fact]
    public void A_readable_interactive_desktop_is_not_a_secure_one()
    {
        if (!InteractiveDesktop.IsReadable)
        {
            return; // locked, the secure desktop, or a disconnected session: nothing to assert against.
        }

        SecureDesktopState state = new WindowsSecureDesktopMonitor(NullLogger.Instance).Poll();
        state.Kind.ShouldBe(SecureDesktopKind.None);
        state.OnSecureDesktop.ShouldBeFalse("a readable ordinary desktop is not the secure one");
    }
}
