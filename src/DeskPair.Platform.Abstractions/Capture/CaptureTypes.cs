namespace DeskPair.Platform.Abstractions.Capture;

public enum PixelFormat
{
    Bgra32,
    Rgba32,
    Nv12,
    I420,
}

public enum FrameRotation
{
    None = 0,
    Rotate90 = 90,
    Rotate180 = 180,
    Rotate270 = 270,
}

public enum GpuApi
{
    None,
    D3D11,
    Metal,
    OpenGl,
    Vaapi,
}

/// <summary>
/// Opaque reference to a GPU surface owned by the producing component.
/// <see cref="Handle"/> is the native object (ID3D11Texture2D*, IOSurfaceRef, VASurfaceID);
/// <see cref="SharedHandle"/> is a cross-process/cross-API handle (NT handle, dma-buf fd) when available.
/// </summary>
public readonly record struct GpuSurfaceHandle(GpuApi Api, nint Handle, nint SharedHandle, uint FourCc)
{
    public static GpuSurfaceHandle None => default;
    public bool IsValid => Api != GpuApi.None && Handle != 0;
}

public readonly record struct DisplayDescriptor(
    int Index,
    string Name,
    int X,
    int Y,
    int Width,
    int Height,
    double Scale,
    FrameRotation Rotation,
    bool IsPrimary,
    long AdapterLuid);

/// <summary>Axis-aligned rectangle in display pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// One captured frame. CPU-backed frames borrow <see cref="Cpu"/> until the capturer produces its next
/// <see cref="CaptureStatus.Frame"/> (a timeout or error never overwrites it, so the caller may keep using
/// the last picture for repeats and refinement without copying); GPU frames reference a surface that
/// stays valid for the same window.
/// </summary>
public readonly struct CaptureFrame
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required PixelFormat Format { get; init; }
    public FrameRotation Rotation { get; init; }
    public long TimestampTicks { get; init; }

    /// <summary>Regions that changed since the previous frame when the capturer knows them; empty means "unknown, assume everything".</summary>
    public ReadOnlyMemory<PixelRect> DirtyRects { get; init; }

    public ReadOnlyMemory<byte> Cpu { get; init; }
    public int Stride { get; init; }

    public GpuSurfaceHandle Gpu { get; init; }
    public bool IsGpuTexture => Gpu.IsValid;
}

public enum CaptureStatus
{
    Frame,
    /// <summary>No new frame within the timeout; the screen did not change.</summary>
    Timeout,
    /// <summary>The input desktop changed (Windows secure desktop / session switch); rebuild the capturer.</summary>
    DesktopSwitched,
    Error,
}

public readonly record struct CaptureResult(CaptureStatus Status, CaptureFrame Frame, Exception? Error = null)
{
    public static CaptureResult TimedOut => new(CaptureStatus.Timeout, default);
    public static CaptureResult Switched => new(CaptureStatus.DesktopSwitched, default);
    public static CaptureResult Failed(Exception e) => new(CaptureStatus.Error, default, e);
}
