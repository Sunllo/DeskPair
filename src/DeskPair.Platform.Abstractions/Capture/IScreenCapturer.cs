namespace DeskPair.Platform.Abstractions.Capture;

public interface IDisplayEnumerator
{
    IReadOnlyList<DisplayDescriptor> GetDisplays();

    event EventHandler? DisplaysChanged;
}

public interface IScreenCapturerFactory
{
    IScreenCapturer Create(DisplayDescriptor display, bool preferGpu);
}

public interface IScreenCapturer : IAsyncDisposable
{
    DisplayDescriptor Display { get; }

    bool SupportsGpuTexture { get; }

    GpuApi GpuApi { get; }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a changed frame. Returns
    /// <see cref="CaptureStatus.Timeout"/> when nothing changed; frame-rate pacing is the caller's job.
    /// </summary>
    ValueTask<CaptureResult> AcquireFrameAsync(TimeSpan timeout, CancellationToken ct);

    /// <summary>Switches to the slower universal path (e.g. GDI on Windows) after the fast path failed.</summary>
    void ForceFallbackPath();

    /// <summary>
    /// How the last frame's time was spent: waiting for the desktop to present something new, and copying the
    /// picture out of the GPU. A high wait means the desktop itself is slow (a virtual or remote display composes
    /// far below the monitor's rate), not the capture path. Zero when the implementation does not measure it.
    /// </summary>
    (double WaitMs, double ReadbackMs) LastFrameTiming => (0, 0);
}
