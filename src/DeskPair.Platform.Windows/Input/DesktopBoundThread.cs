using System.Collections.Concurrent;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Input;

/// <summary>
/// One thread, owned by this object, attached to whichever desktop is taking input, and used for nothing
/// else.
///
/// Injection is desktop-bound, and the binding is a property of the *thread*: SendInput goes to the input
/// queue of the desktop the calling thread is attached to, and SetThreadDesktop attaches one thread.
/// Reading is bound the same way -- GetForegroundWindow and GetKeyState answer about the caller's desktop,
/// not about the screen.
///
/// Which is why this cannot be done on whatever thread the message arrived on. Those are thread-pool
/// threads: the next keystroke may well run on a different one, still attached to Default, so typing into
/// the lock screen would work sometimes and not others -- and the ones that were switched stay switched,
/// carrying an attachment to the Winlogon desktop into whatever unrelated work the pool gives them next.
///
/// So: a thread of its own, which never creates a window or sets a hook, because SetThreadDesktop refuses
/// a thread that has either. It re-asks before every batch rather than caching, since the desktop changes
/// whenever the screen locks or a prompt goes up -- and because asking every time means never having to
/// know the answer in advance.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopBoundThread : IDisposable
{
    private readonly BlockingCollection<Action> _work = new();
    private readonly ILogger _log;
    private readonly Thread _thread;
    private nint _desktop;
    private bool _complained;

    public DesktopBoundThread(ILogger log)
    {
        _log = log;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "deskpair-input",
        };
        _thread.Start();
    }

    /// <summary>Queues work and returns. Injection wants this: it is a stream, and it must keep its order.</summary>
    public void Post(Action work)
    {
        try
        {
            _work.Add(work);
        }
        catch (InvalidOperationException)
        {
            // Disposed while a session was still sending. Dropping the event is right; the session is over.
        }
    }

    /// <summary>Queues work and waits for it. For the few calls whose answer the caller needs.</summary>
    public T Call<T>(Func<T> work)
    {
        using var done = new ManualResetEventSlim(false);
        T result = default!;
        Exception? failure = null;
        Post(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception e)
            {
                failure = e;
            }
            finally
            {
                done.Set();
            }
        });

        // Bounded, because a caller of this is on a session's message pump and a thread that has somehow
        // stopped draining must not take the session down with it.
        if (!done.Wait(TimeSpan.FromSeconds(2)))
        {
            _log.LogWarning("The input thread did not answer within two seconds");
            return default!;
        }

        return failure is null ? result : throw failure;
    }

    private void Loop()
    {
        foreach (Action work in _work.GetConsumingEnumerable())
        {
            Attach();
            try
            {
                work();
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Injecting input failed");
            }
        }

        if (_desktop != 0)
        {
            User32.CloseDesktop(_desktop);
            _desktop = 0;
        }
    }

    /// <summary>
    /// Attaches this thread to the desktop currently taking input, if it is not already.
    /// </summary>
    private void Attach()
    {
        nint desktop = User32.OpenInputDesktop(0, 0, User32.GENERIC_ALL);
        if (desktop == 0)
        {
            // Only an engine running as LocalSystem may open the Winlogon desktop, so this is the ordinary
            // answer when the app hosts the engine and the screen is locked. Said once: a silent refusal
            // here is indistinguishable from input that is being delivered and ignored.
            if (!_complained)
            {
                _complained = true;
                _log.LogWarning(
                    "Cannot attach to the input desktop (error {Error}); input will only reach the desktop this process started on",
                    System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            }

            return;
        }

        if (_complained)
        {
            _complained = false;
            _log.LogInformation("The input desktop can be attached to again");
        }

        if (desktop == _desktop)
        {
            User32.CloseDesktop(desktop);
            return;
        }

        if (User32.SetThreadDesktop(desktop) == 0)
        {
            _log.LogWarning(
                "SetThreadDesktop refused (error {Error}); staying on the current desktop",
                System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
            User32.CloseDesktop(desktop);
            return;
        }

        // Only after the switch has succeeded: closing the desktop this thread is still on would be
        // closing the handle out from under it.
        if (_desktop != 0)
        {
            User32.CloseDesktop(_desktop);
        }

        _desktop = desktop;
    }

    public void Dispose()
    {
        _work.CompleteAdding();

        // Long enough for a queued release-all to land, short enough not to hold up a shutdown.
        _thread.Join(TimeSpan.FromSeconds(2));
        _work.Dispose();
    }
}
