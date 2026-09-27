using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DeskPair.Tools.PlatformHarness;

/// <summary>
/// Times the GPU-to-CPU path that <c>DxgiScreenCapturer</c> pays on every delivered frame, so the cost of a
/// zero-copy path can be argued from numbers rather than from theory. A live capture cannot answer this on an
/// idle desktop (no frames are presented, so nothing is read back), and on a busy one the readback time is
/// mixed in with whatever else the pipeline was doing. Here the source texture is ours, so the loop runs at
/// whatever rate the hardware allows and each step is timed on its own.
///
/// The steps mirror the capturer exactly: CopyResource into a staging texture, Map (which is where the
/// transfer actually lands, because the copy is queued), a row-by-row copy into a managed buffer, Unmap.
/// The CPU BGRA-to-NV12 convert that follows in <c>VideoService</c> is timed alongside it, because a GPU
/// path would remove both or neither.
/// </summary>
internal static class ReadbackBenchmark
{
    public static void Run(int adapterIndex, int iterations, (int W, int H)[] sizes, ILoggerFactory? encodeWith)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        factory.EnumAdapters1((uint)adapterIndex, out IDXGIAdapter1? adapter).CheckError();
        using (adapter)
        {
            AdapterDescription1 ad = adapter!.Description1;
            Console.WriteLine($"adapter {adapterIndex}: {ad.Description.Trim()} ({ad.DedicatedVideoMemory / (1024 * 1024)} MB dedicated)");
            Console.WriteLine($"{iterations} timed iterations per size (plus 10 warm-up)");
            Console.WriteLine();

            D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
                out ID3D11Device? device).CheckError();
            using (device)
            using (ID3D11DeviceContext context = device!.ImmediateContext)
            {
                foreach ((int w, int h) in sizes)
                {
                    Measure(device, context, w, h, iterations, encodeWith);
                }
            }
        }
    }

    private static void Measure(ID3D11Device device, ID3D11DeviceContext context, int width, int height, int iterations, ILoggerFactory? encodeWith)
    {
        // The production numbers come from a session where NVENC was encoding the previous frame while this
        // one was being read back. Both share the GPU and its command queue, so an idle-GPU readback is a
        // floor, not the number the pipeline actually pays; --encode reproduces the contention.
        IVideoEncoder? encoder = null;
        if (encodeWith is not null)
        {
            encoder = new DeskPair.Platform.Windows.Codec.MfVideoEncoderFactory(encodeWith).Create(
                new VideoEncoderConfig(VideoCodec.H264, width, height, 60, 8000, PreferHardware: true, PixelFormat.Nv12, GpuApi.None, 0));
            Console.WriteLine($"encoding alongside: {encoder.Descriptor}");
        }

        // A desktop texture as duplication hands it over: GPU-local BGRA, no CPU access.
        using ID3D11Texture2D source = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
        });

        using ID3D11Texture2D staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        using ID3D11RenderTargetView rtv = device.CreateRenderTargetView(source);

        int stride = width * 4;
        byte[] buffer = new byte[stride * height];
        byte[] nv12 = new byte[PixelConversion.Nv12Size(width, height)];

        double[] copy = new double[iterations];
        double[] map = new double[iterations];
        double[] memcpy = new double[iterations];
        double[] convert = new double[iterations];
        double[] encode = new double[iterations];

        for (int i = -10; i < iterations; i++)
        {
            // Dirty the source every iteration so no driver can elide the copy, and so the transfer is of
            // pixels the GPU has just written, as it would be after a present.
            context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(
                (i & 7) / 7f, ((i >> 1) & 7) / 7f, ((i >> 2) & 7) / 7f, 1f));

            long t0 = Stopwatch.GetTimestamp();
            context.CopyResource(staging, source);
            long t1 = Stopwatch.GetTimestamp();
            MappedSubresource m = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            long t2 = Stopwatch.GetTimestamp();
            unsafe
            {
                byte* src = (byte*)m.DataPointer;
                for (int y = 0; y < height; y++)
                {
                    new ReadOnlySpan<byte>(src + ((long)y * m.RowPitch), stride).CopyTo(buffer.AsSpan(y * stride, stride));
                }
            }

            long t3 = Stopwatch.GetTimestamp();
            context.Unmap(staging, 0);
            PixelConversion.BgraToNv12(buffer, stride, width, height, nv12);
            long t4 = Stopwatch.GetTimestamp();
            encoder?.TryEncode(nv12, width, i * (TimeSpan.TicksPerSecond / 60), out _);
            long t5 = Stopwatch.GetTimestamp();

            if (i < 0)
            {
                continue;
            }

            copy[i] = Ms(t0, t1);
            map[i] = Ms(t1, t2);
            memcpy[i] = Ms(t2, t3);
            convert[i] = Ms(t3, t4);
            encode[i] = Ms(t4, t5);
        }

        if (encoder is not null)
        {
            encoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        double readback = Median(copy) + Median(map) + Median(memcpy);
        Console.WriteLine($"{width}x{height}  ({stride * height / (1024.0 * 1024.0):F1} MB per frame)");
        Report("  CopyResource (queued)", copy);
        Report("  Map (transfer lands)", map);
        Report("  row copy to managed ", memcpy);
        Console.WriteLine($"  -> readback total   median {readback:F2} ms   ({1000.0 / readback:F0} fps ceiling)");
        Report("  BGRA->NV12 on CPU   ", convert);
        Console.WriteLine($"  -> removed by a GPU path: {readback + Median(convert):F2} ms per frame");
        if (encodeWith is not null)
        {
            Report("  encode submit       ", encode);
        }

        Console.WriteLine();
    }

    private static void Report(string label, double[] samples)
    {
        double[] sorted = [.. samples];
        Array.Sort(sorted);
        Console.WriteLine($"{label} median {Median(samples),6:F2} ms   p95 {sorted[(int)(sorted.Length * 0.95)],6:F2}   max {sorted[^1],6:F2}");
    }

    private static double Median(double[] samples)
    {
        double[] sorted = [.. samples];
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    private static double Ms(long from, long to) => Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
}
