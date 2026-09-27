using DeskPair.Core.Clipboard;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Protocol;

namespace DeskPair.Core.Tests;

public class ClipboardSyncTests
{
    private static ReadOnlyMemory<byte> Bytes(string s) => System.Text.Encoding.UTF8.GetBytes(s);

    [Fact]
    public void Applied_content_is_not_sent_back_and_sent_content_is_not_reapplied()
    {
        var sync = new ClipboardSync();
        var wire = ClipboardSync.ToWire([new ClipboardItem(ClipboardItemFormat.Text, Bytes("hello"))])!;
        byte[] hash = wire.ContentHash.ToByteArray();

        sync.ShouldApply(hash).ShouldBeTrue();
        sync.MarkApplied(hash);
        sync.ShouldSend(hash).ShouldBeFalse();

        var other = ClipboardSync.ToWire([new ClipboardItem(ClipboardItemFormat.Text, Bytes("other"))])!;
        byte[] otherHash = other.ContentHash.ToByteArray();
        sync.ShouldSend(otherHash).ShouldBeTrue();
        sync.MarkSent(otherHash);
        sync.ShouldApply(otherHash).ShouldBeFalse();
        sync.ShouldSend(otherHash).ShouldBeFalse();
    }

    [Fact]
    public void Hash_is_order_independent_and_format_sensitive()
    {
        var a = ClipboardSync.ToWire([new ClipboardItem(ClipboardItemFormat.Text, Bytes("x")), new ClipboardItem(ClipboardItemFormat.Html, Bytes("<b>x</b>"))])!;
        var b = ClipboardSync.ToWire([new ClipboardItem(ClipboardItemFormat.Html, Bytes("<b>x</b>")), new ClipboardItem(ClipboardItemFormat.Text, Bytes("x"))])!;
        var c = ClipboardSync.ToWire([new ClipboardItem(ClipboardItemFormat.Rtf, Bytes("x"))])!;
        a.ContentHash.ShouldBe(b.ContentHash);
        a.ContentHash.ShouldNotBe(c.ContentHash);
    }

    [Fact]
    public void Oversized_and_empty_clipboards_are_dropped()
    {
        ClipboardSync.ToWire([]).ShouldBeNull();
        ClipboardSync.ToWire([new ClipboardItem(ClipboardItemFormat.ImagePng, new byte[ProtocolConstants.MaxClipboardBytes + 1])]).ShouldBeNull();
    }

    [Fact]
    public void An_image_too_large_to_carry_does_not_take_the_text_with_it()
    {
        // A phone screenshot can be larger than the cap. Abandoning the whole clipboard when one item does
        // not fit meant the text beside it vanished too — and which items survived depended on the order
        // the platform happened to list them in.
        var items = new List<ClipboardItem>
        {
            new(ClipboardItemFormat.Text, Bytes("keep me")),
            new(ClipboardItemFormat.ImagePng, new byte[ProtocolConstants.MaxClipboardBytes + 1]),
        };

        IReadOnlyList<ClipboardItem> back = ClipboardSync.FromWire(ClipboardSync.ToWire(items)!);
        back.Count.ShouldBe(1);
        back[0].Format.ShouldBe(ClipboardItemFormat.Text);
        back[0].Payload.ToArray().ShouldBe(Bytes("keep me").ToArray());
    }

    [Fact]
    public void The_order_items_arrive_in_does_not_change_what_survives()
    {
        ClipboardItem text = new(ClipboardItemFormat.Text, Bytes("keep me"));
        ClipboardItem huge = new(ClipboardItemFormat.ImagePng, new byte[ProtocolConstants.MaxClipboardBytes + 1]);

        ClipboardSync.FromWire(ClipboardSync.ToWire([text, huge])!).Count.ShouldBe(1);
        ClipboardSync.FromWire(ClipboardSync.ToWire([huge, text])!).Count.ShouldBe(1);
    }

    [Fact]
    public void Formats_round_trip()
    {
        var items = new List<ClipboardItem>
        {
            new(ClipboardItemFormat.Text, Bytes("t")),
            new(ClipboardItemFormat.Html, Bytes("h")),
            new(ClipboardItemFormat.Rtf, Bytes("r")),
            new(ClipboardItemFormat.ImagePng, new byte[] { 1, 2, 3 }),
        };
        IReadOnlyList<ClipboardItem> back = ClipboardSync.FromWire(ClipboardSync.ToWire(items)!);
        back.Select(i => i.Format).ShouldBe(items.Select(i => i.Format));
        back.Select(i => i.Payload.ToArray()).ShouldBe(items.Select(i => i.Payload.ToArray()));
    }
}
