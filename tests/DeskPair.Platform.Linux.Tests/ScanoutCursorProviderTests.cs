using DeskPair.Platform.Abstractions.Cursor;
using DeskPair.Platform.Linux.Capture;

namespace DeskPair.Platform.Linux.Tests;

public class ScanoutCursorProviderTests
{
    [Fact]
    public void The_shape_is_a_visible_arrow_with_its_hotspot_at_the_tip()
    {
        // The fake that stood here was fully transparent, and a viewer whose own pointer takes that shape
        // has no pointer at all over a Linux host. This one has to be seen.
        var provider = new ScanoutCursorProvider();
        ulong id = provider.GetCurrentCursorId();
        CursorImage image = provider.GetCursorImage(id).ShouldNotBeNull();

        image.Id.ShouldBe(id);
        (image.HotX, image.HotY).ShouldBe((0, 0));
        image.Bgra.Length.ShouldBe(image.Width * image.Height * 4);

        int opaque = 0;
        int white = 0;
        ReadOnlySpan<byte> bgra = image.Bgra.Span;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            if (bgra[i + 3] == 255)
            {
                opaque++;
                if (bgra[i] == 255)
                {
                    white++;
                }
            }
            else
            {
                // Premultiplied: a transparent pixel carries no colour.
                (bgra[i] | bgra[i + 1] | bgra[i + 2] | bgra[i + 3]).ShouldBe(0);
            }
        }

        opaque.ShouldBeGreaterThan(40);
        white.ShouldBeGreaterThan(10);
        bgra[3].ShouldBe((byte)255); // the tip, at the hotspot, is drawn
    }

    [Fact]
    public void It_does_not_guess_where_the_pointer_is()
    {
        // Without a cursor plane the compositor's pointer is not readable, and a made-up position is a
        // second pointer drawn somewhere wrong. The picture carries the real one.
        new ScanoutCursorProvider().GetCursorPosition().ShouldBeNull();
        new ScanoutCursorProvider().GetCursorImage(42).ShouldBeNull();
    }
}
