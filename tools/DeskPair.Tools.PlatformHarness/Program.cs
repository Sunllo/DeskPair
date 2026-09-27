using System.Diagnostics;
using Microsoft.Extensions.Logging;
using DeskPair.Platform.Abstractions.Capture;
using DeskPair.Platform.Abstractions.Codec;
using DeskPair.Platform.Abstractions.Input;

// Manual checks of the native platform layer. Verbs:
//   capture   [--display N] [--seconds S] [--out DIR] [--nodump] [--timeout MS] [--motion]
//             capture frames, report fps, the frame interval spread and the readback split, dump
//             first/last frame as BMP; --motion drives a moving window so an idle desktop still presents
//   displays                                             list displays
//   codecs                                               list the encoders and decoders this machine offers
//   cursor                                               print cursor id/position and dump the shape
//   inject    [--x X --y Y]                              move the mouse and read the position back
//   readback  [--adapter N] [--iterations N] [--size 1920x1080,2560x1440] [--encode]
//             time the GPU->CPU staging copy a captured frame pays, split by step;
//             --encode runs the real H.264 encoder alongside it, as a live session does
if (args.Length == 0)
{
    Console.Error.WriteLine("verbs: displays | codecs | capture | cursor | inject | readback");
    return 1;
}

using ILoggerFactory logs = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Debug));
Dictionary<string, string> o = Parse(args.Skip(1));

var enumerator = new DeskPair.Platform.Windows.Capture.WindowsDisplayEnumerator();
IScreenCapturerFactory capturers = new DeskPair.Platform.Windows.Capture.WindowsScreenCapturerFactory(enumerator, logs) { ForceGdi = o.ContainsKey("gdi") };
IInputInjector injector = new DeskPair.Platform.Windows.Input.WindowsInputInjector(logs.CreateLogger("input"));
DeskPair.Platform.Abstractions.Cursor.ICursorProvider cursor = new DeskPair.Platform.Windows.Input.WindowsCursorProvider();

switch (args[0])
{
    case "displays":
        foreach (DisplayDescriptor d in enumerator.GetDisplays())
        {
            Console.WriteLine($"{d.Index}: {d.Name} {d.Width}x{d.Height} at ({d.X},{d.Y}) scale {d.Scale:F2} rot {d.Rotation} primary={d.IsPrimary} luid={d.AdapterLuid}");
        }

        return 0;

    case "capture":
    {
        int index = int.Parse(o.GetValueOrDefault("display", "0"));
        double seconds = double.Parse(o.GetValueOrDefault("seconds", "5"));
        string outDir = o.GetValueOrDefault("out", Path.Combine(Path.GetTempPath(), "sunllo-capture"));
        Directory.CreateDirectory(outDir);
        DisplayDescriptor display = enumerator.GetDisplays()[index];
        using var motionStop = new CancellationTokenSource();
        Thread? motion = null;
        if (o.ContainsKey("motion"))
        {
            motion = new Thread(() => DeskPair.Tools.PlatformHarness.MotionWindow.RunUntil(
                motionStop.Token, display.X + (display.Width / 2), display.Y + (display.Height / 2), display.Height / 4))
            { IsBackground = true };
            motion.Start();
            Console.WriteLine("driving a moving window so the desktop has something to present");
        }

        await using IScreenCapturer capturer = capturers.Create(display, preferGpu: false);
        Console.WriteLine($"capturing {display.Name} {display.Width}x{display.Height} for {seconds}s ({(capturer is DeskPair.Platform.Windows.Capture.DxgiScreenCapturer { IsGdi: true } ? "GDI" : "DXGI")})");
        bool dump = !o.ContainsKey("nodump");
        var timeout = TimeSpan.FromMilliseconds(double.Parse(o.GetValueOrDefault("timeout", "33")));
        var sw = Stopwatch.StartNew();
        int frames = 0, timeouts = 0;
        byte[]? first = null, last = null;
        int w = 0, h = 0, stride = 0;
        var intervals = new List<double>();
        var frameCalls = new List<double>();
        var waits = new List<double>();
        var readbacks = new List<double>();
        var copies = new List<double>();
        var metadata = new List<double>();
        var maps = new List<double>();
        var timeoutCalls = new List<double>();
        double previous = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            double callStart = sw.Elapsed.TotalMilliseconds;
            CaptureResult r = await capturer.AcquireFrameAsync(timeout, CancellationToken.None);
            double callMs = sw.Elapsed.TotalMilliseconds - callStart;
            (r.Status == CaptureStatus.Frame ? frameCalls : timeoutCalls).Add(callMs);
            if (r.Status == CaptureStatus.Frame && capturer is DeskPair.Platform.Windows.Capture.DxgiScreenCapturer dxgi)
            {
                waits.Add(dxgi.LastWaitMs);
                readbacks.Add(dxgi.LastReadbackMs);
                (double c, double m, double mp) = dxgi.LastReadbackParts;
                copies.Add(c);
                metadata.Add(m);
                maps.Add(mp);
            }
            switch (r.Status)
            {
                case CaptureStatus.Frame:
                    frames++;
                    double at = sw.Elapsed.TotalMilliseconds;
                    if (frames == 1)
                    {
                        // What somebody who just connected waits in front of a black window.
                        Console.WriteLine($"first frame after {at:F0} ms");
                    }

                    if (previous > 0)
                    {
                        intervals.Add(at - previous);
                    }

                    previous = at;
                    w = r.Frame.Width;
                    h = r.Frame.Height;
                    stride = r.Frame.Stride;
                    if (dump)
                    {
                        last = r.Frame.Cpu.ToArray();
                        first ??= last;
                    }

                    break;
                case CaptureStatus.Timeout:
                    timeouts++;
                    break;
                default:
                    Console.WriteLine($"status {r.Status}: {r.Error?.Message}");
                    break;
            }
        }

        Console.WriteLine($"{frames} frames ({frames / sw.Elapsed.TotalSeconds:F1} fps), {timeouts} unchanged polls");
        if (frameCalls.Count > 0)
        {
            frameCalls.Sort();
            Console.WriteLine($"acquire call ms (frames): p50 {frameCalls[frameCalls.Count / 2]:F1}, p90 {frameCalls[(int)(frameCalls.Count * 0.9)]:F1}, max {frameCalls[^1]:F1}");
        }

        if (timeoutCalls.Count > 0)
        {
            timeoutCalls.Sort();
            Console.WriteLine($"acquire call ms (no change): p50 {timeoutCalls[timeoutCalls.Count / 2]:F1}, max {timeoutCalls[^1]:F1}");
        }

        if (waits.Count > 0)
        {
            waits.Sort();
            readbacks.Sort();
            Console.WriteLine($"waiting for a new desktop frame ms: p50 {waits[waits.Count / 2]:F1}, p90 {waits[(int)(waits.Count * 0.9)]:F1}");
            Console.WriteLine($"GPU readback ms: p50 {readbacks[readbacks.Count / 2]:F1}, p90 {readbacks[(int)(readbacks.Count * 0.9)]:F1}");
            static string Split(string label, List<double> v)
            {
                v.Sort();
                return $"  {label}: p50 {v[v.Count / 2]:F2}, p90 {v[(int)(v.Count * 0.9)]:F2}";
            }

            Console.WriteLine(Split("acquire+copy submit", copies));
            Console.WriteLine(Split("dirty rects+release", metadata));
            Console.WriteLine(Split("map+memcpy         ", maps));
        }

        if (intervals.Count > 0)
        {
            intervals.Sort();
            double Pick(double q) => intervals[Math.Clamp((int)(q * intervals.Count), 0, intervals.Count - 1)];
            // Frames closer together than 200 ms are one burst of movement: the rate that matters while dragging.
            List<double> moving = intervals.Where(i => i < 200).ToList();
            Console.WriteLine($"frame intervals ms: p50 {Pick(0.5):F1}, p90 {Pick(0.9):F1}, min {intervals[0]:F1}, max {intervals[^1]:F1}");
            Console.WriteLine(moving.Count > 0
                ? $"while moving: {moving.Count} intervals, {1000 / moving.Average():F1} fps (mean interval {moving.Average():F1} ms)"
                : "while moving: nothing moved");
        }

        if (first is not null)
        {
            Bmp.Write(Path.Combine(outDir, "first.bmp"), first, w, h, stride);
            Bmp.Write(Path.Combine(outDir, "last.bmp"), last!, w, h, stride);
            Console.WriteLine($"wrote {outDir}\\first.bmp and last.bmp");
        }

        motionStop.Cancel();
        motion?.Join(TimeSpan.FromSeconds(2));
        return frames > 0 ? 0 : 3;
    }

    case "cursor":
    {
        ulong id = cursor.GetCurrentCursorId();
        Console.WriteLine($"cursor id {id:X} at {cursor.GetCursorPosition()}");
        DeskPair.Platform.Abstractions.Cursor.CursorImage? img = cursor.GetCursorImage(id);
        if (img is not null)
        {
            string path = Path.Combine(Path.GetTempPath(), "sunllo-cursor.bmp");
            Bmp.Write(path, img.Bgra.ToArray(), img.Width, img.Height, img.Width * 4);
            Console.WriteLine($"{img.Width}x{img.Height} hotspot ({img.HotX},{img.HotY}) -> {path}");
        }

        return 0;
    }

    case "inject":
    {
        int x = int.Parse(o.GetValueOrDefault("x", "200"));
        int y = int.Parse(o.GetValueOrDefault("y", "200"));
        IReadOnlyList<DisplayDescriptor> displays = enumerator.GetDisplays();
        var virt = new VirtualScreenRect(displays.Min(d => d.X), displays.Min(d => d.Y), displays.Max(d => d.X + d.Width) - displays.Min(d => d.X), displays.Max(d => d.Y + d.Height) - displays.Min(d => d.Y));
        injector.EnsureInputDesktop();
        injector.InjectMouse(new MouseInput(MouseAction.Move, MouseButtons.None, x, y, 0), virt);
        await Task.Delay(50);
        (int X, int Y)? pos = cursor.GetCursorPosition();
        Console.WriteLine($"asked ({x},{y}) -> cursor at {pos}");
        return pos is { } p && Math.Abs(p.X - x) <= 1 && Math.Abs(p.Y - y) <= 1 ? 0 : 4;
    }

    case "codecs":
    {
        IVideoEncoderFactory encoders = new FallbackVideoEncoderFactory(
            new DeskPair.Platform.Windows.Codec.MfVideoEncoderFactory(logs),
            new DeskPair.Codec.Vpx.VpxVideoEncoderFactory(logs));
        Console.WriteLine("encoders:");
        foreach (EncoderDescriptor d in encoders.Describe())
        {
            Console.WriteLine($"  {d}");
        }

        Console.WriteLine($"  -> {encoders.Probe()}");
        IVideoDecoderFactory decoders = new FallbackVideoDecoderFactory(
            new DeskPair.Platform.Windows.Codec.MfVideoDecoderFactory(logs),
            new DeskPair.Codec.Vpx.VpxVideoDecoderFactory(logs));
        Console.WriteLine($"decoders: {decoders.Probe()}");

        // Probing says what is registered; only creating one says whether it will actually take our stream.
        foreach (VideoCodec c in Enum.GetValues<VideoCodec>())
        {
            if (!decoders.Probe().Supports(c))
            {
                continue;
            }

            try
            {
                decoders.Create(c, GpuApi.None, 0).DisposeAsync().AsTask().GetAwaiter().GetResult();
                Console.WriteLine($"  {c}: created");
            }
            catch (Exception e)
            {
                Console.WriteLine($"  {c}: {e.GetType().Name}: {e.Message}");
                for (Exception? inner = e.InnerException; inner is not null; inner = inner.InnerException)
                {
                    Console.WriteLine($"      caused by {inner.GetType().Name}: {inner.Message}");
                }
            }
        }

        return 0;
    }

    case "readback":
    {
        int adapter = int.Parse(o.GetValueOrDefault("adapter", "0"));
        int iterations = int.Parse(o.GetValueOrDefault("iterations", "200"));
        (int, int)[] sizes = o.TryGetValue("size", out string? s)
            ? [.. s.Split(',').Select(p => (int.Parse(p.Split('x')[0]), int.Parse(p.Split('x')[1])))]
            : [(1920, 1080), (2560, 1440), (3840, 2160)];
        DeskPair.Tools.PlatformHarness.ReadbackBenchmark.Run(adapter, iterations, sizes, o.ContainsKey("encode") ? logs : null);
        return 0;
    }

    default:
        Console.Error.WriteLine($"unknown verb {args[0]}");
        return 1;
}

static Dictionary<string, string> Parse(IEnumerable<string> args)
{
    var result = new Dictionary<string, string>();
    string? key = null;
    foreach (string a in args)
    {
        if (a.StartsWith("--"))
        {
            if (key is not null)
            {
                result[key] = string.Empty;
            }

            key = a[2..];
        }
        else if (key is not null)
        {
            result[key] = a;
            key = null;
        }
    }

    if (key is not null)
    {
        result[key] = string.Empty;
    }

    return result;
}

internal static class Bmp
{
    /// <summary>Writes a top-down BGRA32 buffer as a 32-bit BMP.</summary>
    public static void Write(string path, byte[] bgra, int width, int height, int stride)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var w = new BinaryWriter(fs);
        int rowBytes = width * 4;
        int imageSize = rowBytes * height;
        w.Write((ushort)0x4D42);
        w.Write(54 + imageSize);
        w.Write(0);
        w.Write(54);
        w.Write(40);
        w.Write(width);
        w.Write(-height);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(0);
        w.Write(imageSize);
        w.Write(2835);
        w.Write(2835);
        w.Write(0);
        w.Write(0);
        for (int y = 0; y < height; y++)
        {
            w.Write(bgra, y * stride, rowBytes);
        }
    }
}
