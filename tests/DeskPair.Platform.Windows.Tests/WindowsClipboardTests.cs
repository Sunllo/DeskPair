using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Abstractions.Imaging;
using DeskPair.Platform.Windows.Clipboard;

namespace DeskPair.Platform.Windows.Tests;

[Collection("clipboard")]
public class WindowsClipboardTests
{
    [Fact]
    public async Task Text_html_and_image_round_trip_and_own_writes_are_not_echoed()
    {
        if (!InteractiveDesktop.IsAvailable)
        {
            return; // OpenClipboard is refused on a disconnected session or a non-interactive window station
        }

        await using var clipboard = new WindowsClipboard(NullLogger.Instance);
        byte[] bgra = new byte[16 * 8 * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)i;
            bgra[i + 1] = 0x80;
            bgra[i + 2] = 0x20;
            bgra[i + 3] = 255;
        }

        string text = $"Sunllo 快桌 ✓ {Guid.NewGuid():N}";
        var items = new List<ClipboardItem>
        {
            new(ClipboardItemFormat.Text, Encoding.UTF8.GetBytes(text)),
            new(ClipboardItemFormat.Html, Encoding.UTF8.GetBytes("<b>bold</b> 中文")),
            new(ClipboardItemFormat.ImagePng, PngCodec.Encode(bgra, 16 * 4, 16, 8)),
        };
        await clipboard.WriteAsync(items, CancellationToken.None);

        IReadOnlyList<ClipboardItem> back = await clipboard.ReadAsync(CancellationToken.None);
        Encoding.UTF8.GetString(back.Single(i => i.Format == ClipboardItemFormat.Text).Payload.Span).ShouldBe(text);
        Encoding.UTF8.GetString(back.Single(i => i.Format == ClipboardItemFormat.Html).Payload.Span).ShouldBe("<b>bold</b> 中文");
        BgraImage image = PngCodec.Decode(back.Single(i => i.Format == ClipboardItemFormat.ImagePng).Payload.Span);
        image.Width.ShouldBe(16);
        image.Height.ShouldBe(8);
        image.Pixels.ShouldBe(bgra);

        // Our own write must not surface as a change. Somebody else's may: the clipboard belongs to the whole machine.
        // The first version of this took any change inside these 300 ms for an echo, and one arrived on a CI runner;
        // the question is about this test's own text, as in the test below.
        using var cts = new CancellationTokenSource(300);
        bool echoed = false;
        try
        {
            while (true)
            {
                echoed |= Carries(await clipboard.Changes.ReadAsync(cts.Token), text);
            }
        }
        catch (OperationCanceledException)
        {
        }

        echoed.ShouldBeFalse("a writer does not hear its own write");
    }

    /// <summary>
    /// A host that is also a controller has two of these in one process. The second used to inherit the
    /// first's window procedure through the shared class name, so it never heard a change: the controller
    /// received everything the host copied and sent nothing back.
    /// </summary>
    [Fact]
    public async Task A_second_instance_in_the_same_process_hears_changes_too()
    {
        if (!InteractiveDesktop.IsAvailable)
        {
            return;
        }

        await using var engine = new WindowsClipboard(NullLogger.Instance);
        await using var controller = new WindowsClipboard(NullLogger.Instance);
        string text = $"two listeners {Guid.NewGuid():N}";

        await engine.WriteAsync([new ClipboardItem(ClipboardItemFormat.Text, Encoding.UTF8.GetBytes(text))], CancellationToken.None);

        // The clipboard belongs to the whole machine: whoever is using it, and any other program, may write
        // to it during the test. So the assertions are about this test's own text, not about the first change.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!Carries(await controller.Changes.ReadAsync(timeout.Token), text))
        {
        }

        while (engine.Changes.TryRead(out IReadOnlyList<ClipboardItem>? own))
        {
            Carries(own, text).ShouldBeFalse("a writer does not hear its own write");
        }
    }

    /// <summary>
    /// Many writes in a row, none of which may come back. Windows announces a write to the listener window's own
    /// thread, which could read the new sequence number before the writer had stored it, and the write came back as
    /// somebody else's change -- in the two tests above now and then, and so on a CI runner. Two hundred writes make that
    /// window wide enough to fall into every time.
    /// </summary>
    [Fact]
    public async Task Two_hundred_writes_in_a_row_never_come_back_as_changes()
    {
        if (!InteractiveDesktop.IsAvailable)
        {
            return;
        }

        await using var clipboard = new WindowsClipboard(NullLogger.Instance);
        string run = Guid.NewGuid().ToString("N");
        for (int i = 0; i < 200; i++)
        {
            await clipboard.WriteAsync([new ClipboardItem(ClipboardItemFormat.Text, Encoding.UTF8.GetBytes($"{run} {i}"))], CancellationToken.None);
        }

        // An echo would carry one of these; another program's change may arrive as well, and is not the question.
        using var settle = new CancellationTokenSource(500);
        var echoed = new List<string>();
        try
        {
            while (true)
            {
                echoed.AddRange((await clipboard.Changes.ReadAsync(settle.Token))
                    .Where(i => i.Format == ClipboardItemFormat.Text)
                    .Select(i => Encoding.UTF8.GetString(i.Payload.Span))
                    .Where(t => t.StartsWith(run, StringComparison.Ordinal)));
            }
        }
        catch (OperationCanceledException)
        {
        }

        echoed.ShouldBeEmpty("a writer does not hear its own writes");
    }

    private static bool Carries(IReadOnlyList<ClipboardItem> items, string text) =>
        items.Any(i => i.Format == ClipboardItemFormat.Text && Encoding.UTF8.GetString(i.Payload.Span) == text);

    [Fact]
    public void Html_format_envelope_round_trips()
    {
        byte[] wrapped = WindowsClipboard.BuildHtmlFormat("<p>hi 你好</p>");
        WindowsClipboard.StripHtmlHeader(wrapped).ShouldBe("<p>hi 你好</p>");
        WindowsClipboard.StripHtmlHeader(Encoding.UTF8.GetBytes("Version:1.0\r\nStartHTML:-1\r\n<html><body>x</body></html>")).ShouldBe("<html><body>x</body></html>");
    }

    [Fact]
    public void Dib_conversion_round_trips_bottom_up_32bpp()
    {
        byte[] bgra = new byte[3 * 2 * 4];
        for (int i = 0; i < bgra.Length; i++)
        {
            bgra[i] = (byte)(i * 7);
        }

        for (int i = 3; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;
        }

        byte[] dib = WindowsClipboard.ToDib(new BgraImage(3, 2, bgra));
        byte[]? png = WindowsClipboard.DibToPng(dib);
        png.ShouldNotBeNull();
        PngCodec.Decode(png).Pixels.ShouldBe(bgra);
    }
}
