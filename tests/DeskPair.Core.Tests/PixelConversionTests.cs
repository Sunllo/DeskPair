using System.Diagnostics;
using System.Reflection;
using DeskPair.Platform.Abstractions.Capture;
using Xunit.Abstractions;

namespace DeskPair.Core.Tests;

public class PixelConversionTests(ITestOutputHelper output)
{
    private static byte[] RandomBgra(int width, int height, int stride, int seed)
    {
        var rng = new Random(seed);
        byte[] b = new byte[stride * height];
        rng.NextBytes(b);
        return b;
    }

    public static TheoryData<int, int> Sizes => new()
    {
        { 1, 1 }, { 3, 2 }, { 17, 5 }, { 31, 3 }, { 32, 2 }, { 64, 4 }, { 647, 9 }, { 640, 360 }, { 100, 1 },
    };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Bgra_to_nv12_matches_the_scalar_reference(int width, int height)
    {
        int stride = width * 4 + 12; // padded stride
        byte[] bgra = RandomBgra(width, height, stride, width * 31 + height);
        byte[] fast = new byte[PixelConversion.Nv12Size(width, height)];
        byte[] slow = new byte[fast.Length];
        PixelConversion.BgraToNv12(bgra, stride, width, height, fast);
        PixelConversion.BgraToNv12Reference(bgra, stride, width, height, slow);
        fast.ShouldBe(slow);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Nv12_to_bgra_matches_the_scalar_reference_including_coded_height(int width, int height)
    {
        int yStride = width + (width & 1) + 16;
        int codedHeight = height + 8; // decoder padding below the visible picture
        var rng = new Random(width * 7 + height);
        byte[] nv12 = new byte[yStride * codedHeight + yStride * ((codedHeight + 1) / 2)];
        rng.NextBytes(nv12);
        int uvOffset = yStride * codedHeight;
        byte[] fast = new byte[width * 4 * height];
        byte[] slow = new byte[fast.Length];
        PixelConversion.Nv12ToBgra(nv12, yStride, uvOffset, width, height, fast, width * 4);
        PixelConversion.Nv12ToBgraReference(nv12, yStride, uvOffset, width, height, slow, width * 4);
        fast.ShouldBe(slow);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void I420_to_bgra_agrees_with_nv12(int width, int height)
    {
        int cw = (width + 1) / 2, ch = (height + 1) / 2;
        var rng = new Random(width + height * 13);
        byte[] y = new byte[width * height];
        byte[] u = new byte[cw * ch];
        byte[] v = new byte[cw * ch];
        rng.NextBytes(y);
        rng.NextBytes(u);
        rng.NextBytes(v);

        int yStride = width + (width & 1);
        byte[] nv12 = new byte[yStride * height + yStride * ch];
        for (int r = 0; r < height; r++)
        {
            y.AsSpan(r * width, width).CopyTo(nv12.AsSpan(r * yStride));
        }

        for (int r = 0; r < ch; r++)
        {
            for (int c = 0; c < cw; c++)
            {
                nv12[yStride * height + r * yStride + c * 2] = u[r * cw + c];
                nv12[yStride * height + r * yStride + c * 2 + 1] = v[r * cw + c];
            }
        }

        byte[] fromI420 = new byte[width * 4 * height];
        byte[] fromNv12 = new byte[fromI420.Length];
        PixelConversion.I420ToBgra(y, width, u, v, cw, width, height, fromI420, width * 4);
        PixelConversion.Nv12ToBgraReference(nv12, yStride, yStride * height, width, height, fromNv12, width * 4);
        fromI420.ShouldBe(fromNv12);
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Bgra_to_i420_is_the_nv12_planes_split(int width, int height)
    {
        int stride = width * 4;
        byte[] bgra = RandomBgra(width, height, stride, width ^ height);
        byte[] i420 = new byte[PixelConversion.I420Size(width, height)];
        byte[] nv12 = new byte[PixelConversion.Nv12Size(width, height)];
        PixelConversion.BgraToI420(bgra, stride, width, height, i420);
        PixelConversion.BgraToNv12Reference(bgra, stride, width, height, nv12);

        int cw = (width + 1) / 2, ch = (height + 1) / 2;
        i420.AsSpan(0, width * height).ToArray().ShouldBe(nv12.AsSpan(0, width * height).ToArray());
        for (int r = 0; r < ch; r++)
        {
            for (int c = 0; c < cw; c++)
            {
                i420[width * height + r * cw + c].ShouldBe(nv12[width * height + r * width + c * 2]);
                int vIndex = c * 2 + 1 < width ? c * 2 + 1 : c * 2;
                i420[width * height + cw * ch + r * cw + c].ShouldBe(nv12[width * height + r * width + vIndex]);
            }
        }
    }

    [Fact]
    public void Full_frame_conversion_cost_is_reported()
    {
        const int W = 2560, H = 1440;
        byte[] bgra = RandomBgra(W, H, W * 4, 5);
        byte[] nv12 = new byte[PixelConversion.Nv12Size(W, H)];
        byte[] back = new byte[W * H * 4];
        PixelConversion.BgraToNv12(bgra, W * 4, W, H, nv12);
        PixelConversion.Nv12ToBgra(nv12, W, W, H, back, W * 4);

        // The best of several runs, not the mean. The whole suite runs its projects in parallel, so any one
        // iteration can be descheduled for longer than the work itself takes; averaging that in measures the
        // machine's load rather than the kernel, and turns a real comparison into a coin toss.
        static double Best(int runs, Action work)
        {
            double best = double.MaxValue;
            for (int i = 0; i < runs; i++)
            {
                long start = Stopwatch.GetTimestamp();
                work();
                best = Math.Min(best, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }

            return best;
        }

        // And in rounds, done at the first that shows the vectorized kernel ahead: a runner that stalled through all ten
        // runs of one kernel and not through the three of the other (a Windows CI runner, once) says nothing about the
        // kernels, while a vectorized path that has really fallen behind loses every round.
        double toNv12 = 0, toBgra = 0, reference = 0;
        for (int round = 0; round < 3 && !(toNv12 < reference); round++)
        {
            toNv12 = Best(10, () => PixelConversion.BgraToNv12(bgra, W * 4, W, H, nv12));
            toBgra = Best(10, () => PixelConversion.Nv12ToBgra(nv12, W, W, H, back, W * 4));
            reference = Best(3, () => PixelConversion.BgraToNv12Reference(bgra, W * 4, W, H, nv12));
        }

        output.WriteLine($"{W}x{H} (best of): BGRA->NV12 {toNv12:F2} ms, NV12->BGRA {toBgra:F2} ms (vectorized {PixelConversion.IsVectorized}); scalar BGRA->NV12 {reference:F1} ms");
        bool optimized = typeof(PixelConversion).Assembly.GetCustomAttribute<DebuggableAttribute>()?.IsJITOptimizerDisabled != true;
        if (PixelConversion.IsVectorized && optimized)
        {
            toNv12.ShouldBeLessThan(reference); // Debug builds do not inline the vector helpers; only Release is meaningful
        }
    }
}
