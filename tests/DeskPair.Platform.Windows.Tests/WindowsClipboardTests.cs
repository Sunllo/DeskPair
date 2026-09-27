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

        var items = new List<ClipboardItem>
        {
            new(ClipboardItemFormat.Text, Encoding.UTF8.GetBytes("Sunllo 快桌 ✓")),
            new(ClipboardItemFormat.Html, Encoding.UTF8.GetBytes("<b>bold</b> 中文")),
            new(ClipboardItemFormat.ImagePng, PngCodec.Encode(bgra, 16 * 4, 16, 8)),
        };
        await clipboard.WriteAsync(items, CancellationToken.None);

        IReadOnlyList<ClipboardItem> back = await clipboard.ReadAsync(CancellationToken.None);
        Encoding.UTF8.GetString(back.Single(i => i.Format == ClipboardItemFormat.Text).Payload.Span).ShouldBe("Sunllo 快桌 ✓");
        Encoding.UTF8.GetString(back.Single(i => i.Format == ClipboardItemFormat.Html).Payload.Span).ShouldBe("<b>bold</b> 中文");
        BgraImage image = PngCodec.Decode(back.Single(i => i.Format == ClipboardItemFormat.ImagePng).Payload.Span);
        image.Width.ShouldBe(16);
        image.Height.ShouldBe(8);
        image.Pixels.ShouldBe(bgra);

        // Our own write must not surface as a change.
        using var cts = new CancellationTokenSource(300);
        bool echoed = false;
        try
        {
            await clipboard.Changes.ReadAsync(cts.Token);
            echoed = true;
        }
        catch (OperationCanceledException)
        {
        }

        echoed.ShouldBeFalse();
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
