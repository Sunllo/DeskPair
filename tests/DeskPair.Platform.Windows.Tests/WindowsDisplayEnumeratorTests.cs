using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Windows.Capture;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>Read-only against the real monitors; the change notice is exercised by posting the message ourselves.</summary>
public class WindowsDisplayEnumeratorTests
{
    [Fact]
    public void Indices_are_positions_with_the_primary_first()
    {
        if (!InteractiveDesktop.IsAvailable)
        {
            return;
        }

        using var displays = new WindowsDisplayEnumerator();
        IReadOnlyList<DisplayDescriptor> all = displays.GetDisplays();

        all.ShouldNotBeEmpty();
        DisplayOrdering.IsConsistent(all).ShouldBeTrue();
        all[0].IsPrimary.ShouldBeTrue();
        all.Select(d => d.Name).Distinct().Count().ShouldBe(all.Count, "device names identify displays");
    }

    /// <summary>
    /// Nobody listened for WM_DISPLAYCHANGE before, so a monitor plugged in mid-session went unnoticed until
    /// the viewer happened to switch displays. The window only exists once somebody subscribes.
    /// </summary>
    [Fact]
    public async Task A_display_change_message_reaches_the_subscriber()
    {
        using var displays = new WindowsDisplayEnumerator();
        displays.IsListening.ShouldBeFalse("no window until somebody cares");

        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        displays.DisplaysChanged += (_, _) => heard.TrySetResult();
        displays.IsListening.ShouldBeTrue();

        await displays.SimulateDisplayChangeAsync();

        (await Task.WhenAny(heard.Task, Task.Delay(5000))).ShouldBe(heard.Task, "the event was raised");
    }

    [Fact]
    public async Task Disposing_stops_the_listener_and_a_second_instance_still_works()
    {
        var first = new WindowsDisplayEnumerator();
        first.DisplaysChanged += (_, _) => { };
        await first.SimulateDisplayChangeAsync();
        first.Dispose();

        using var second = new WindowsDisplayEnumerator();
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        second.DisplaysChanged += (_, _) => heard.TrySetResult();
        await second.SimulateDisplayChangeAsync();

        (await Task.WhenAny(heard.Task, Task.Delay(5000))).ShouldBe(heard.Task);
    }
}
