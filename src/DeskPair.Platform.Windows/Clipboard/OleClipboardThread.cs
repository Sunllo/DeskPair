using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Clipboard;

/// <summary>
/// Owns the OLE clipboard on its own STA thread.
///
/// Its own thread, and not the clipboard listener's, because every <c>IStream::Read</c> of a promised file
/// arrives here by RPC: a multi-gigabyte paste would otherwise hold up <c>WM_CLIPBOARDUPDATE</c> for as long
/// as it takes, and the session would stop noticing that the clipboard had changed at all.
///
/// The loop is <c>CoWaitForMultipleHandles</c> rather than <c>GetMessage</c> because it has to dispatch COM
/// calls as well as window messages; a message pump alone does not service cross-apartment calls.
/// </summary>
internal sealed class OleClipboardThread : IDisposable
{
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly BlockingCollection<Action> _work = [];
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly nint _wake;

    private PromisedDataObject? _published;
    private nint _publishedPointer;
    private bool _disposed;

    public OleClipboardThread(ILogger log)
    {
        _log = log;
        _wake = Kernel32.CreateEventW(0, manualReset: false, initialState: false, null);
        _thread = new Thread(Run) { Name = "clipboard-ole", IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="action"/> on the OLE thread and waits for it.</summary>
    public void Invoke(Action action)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Add(() =>
        {
            try
            {
                action();
                done.TrySetResult();
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        });

        Kernel32.SetEvent(_wake);
        done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Publishes a data object, replacing whatever this process had on the clipboard before.</summary>
    public void Publish(PromisedDataObject data)
    {
        Invoke(() =>
        {
            ReleasePublished();

            // GetOrCreateComInterfaceForObject returns the CCW's IUnknown; OleSetClipboard is declared to take
            // an IDataObject* and calls straight through the vtable it is handed, so it must be given one.
            nint unknown;
            unsafe
            {
                unknown = (nint)ComInterfaceMarshaller<IDataObjectNative>.ConvertToUnmanaged(data);
            }

            Guid iid = Ole32.IID_IDataObject;
            int hr = Marshal.QueryInterface(unknown, in iid, out nint dataObject);
            Marshal.Release(unknown);
            if (hr != Ole32.S_OK)
            {
                data.Dispose();
                throw new InvalidOperationException($"The data object does not expose IDataObject (0x{hr:X8}).");
            }

            hr = Ole32.OleSetClipboard(dataObject);
            if (hr != Ole32.S_OK)
            {
                Marshal.Release(dataObject);
                data.Dispose();

                // A failed OleSetClipboard leaves OLE's clipboard unusable for the rest of the process, so
                // there is no second attempt to make here.
                throw new InvalidOperationException($"OleSetClipboard failed (0x{hr:X8}).");
            }

            _published = data;
            _publishedPointer = dataObject;
        });
    }

    /// <summary>
    /// Takes our promise off the clipboard. Deliberately not <c>OleFlushClipboard</c>: its job is to render
    /// every format eagerly, which for a promise means downloading the whole selection on the way out.
    /// </summary>
    public void Clear()
    {
        Invoke(() =>
        {
            if (_published is null)
            {
                return;
            }

            if (Ole32.OleIsCurrentClipboard(_publishedPointer) == Ole32.S_OK)
            {
                Ole32.OleSetClipboard(0);
                _log.LogInformation("The promised files are no longer on the clipboard; they were not downloaded");
            }

            ReleasePublished();
        });
    }

    private void ReleasePublished()
    {
        _published?.Dispose();
        _published = null;
        if (_publishedPointer != 0)
        {
            Marshal.Release(_publishedPointer);
            _publishedPointer = 0;
        }
    }

    private void Run()
    {
        int hr = Ole32.OleInitialize(0);
        if (hr is not (Ole32.S_OK or Ole32.S_FALSE))
        {
            _ready.TrySetException(new InvalidOperationException($"OleInitialize failed (0x{hr:X8})."));
            return;
        }

        _ready.TrySetResult();

        try
        {
            while (!_work.IsAddingCompleted)
            {
                while (_work.TryTake(out Action? item))
                {
                    item();
                }

                // Dispatches incoming COM calls and window messages while idle. A plain wait here would stop
                // Explorer from ever reaching the data object.
                Ole32.CoWaitForMultipleHandles(
                    Ole32.COWAIT_DISPATCH_CALLS | Ole32.COWAIT_DISPATCH_WINDOW_MESSAGES,
                    200,
                    1,
                    [_wake],
                    out _);
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // The collection was completed while we were in it; shutting down.
        }
        finally
        {
            ReleasePublished();
            Ole32.OleUninitialize();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            Clear();
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Clearing the OLE clipboard on shutdown failed");
        }

        _disposed = true;
        _work.CompleteAdding();
        Kernel32.SetEvent(_wake);
        _thread.Join(TimeSpan.FromSeconds(2));
        _work.Dispose();
        if (_wake != 0)
        {
            Kernel32.CloseHandle(_wake);
        }
    }
}
