using DeskPair.Platform.Linux.Native;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The rule the clipboard reads and writes X events by: member n sits at n * sizeof(long), and a C long on Linux
/// is the pointer size. These run on 64-bit, where the offsets below are the C structs' own; the 32-bit side (4
/// bytes a member) was checked end to end on ARMv7 on 2026-09-28, both clipboard directions and TARGETS.
/// </summary>
public sealed class XEventBytesTests
{
    [Fact]
    public void A_member_is_a_c_long_wide()
    {
        XEventBytes.At(0).ShouldBe(0);
        XEventBytes.At(5).ShouldBe(5 * nint.Size);
    }

    /// <summary>
    /// XSelectionRequestEvent: int type; unsigned long serial; Bool send_event; Display *display; Window owner;
    /// Window requestor; Atom selection; Atom target; Atom property; Time time. The ints are padded to a long, so on
    /// LP64 the requestor is at 40 and the time at 72 -- the byte offsets this code used before it counted members.
    /// </summary>
    [Fact]
    public void The_selection_request_members_land_where_lp64_puts_them()
    {
        if (nint.Size != 8)
        {
            return;
        }

        (XEventBytes.At(5), XEventBytes.At(6), XEventBytes.At(7), XEventBytes.At(8), XEventBytes.At(9))
            .ShouldBe((40, 48, 56, 64, 72));
    }

    [Fact]
    public void Members_round_trip_without_touching_their_neighbours()
    {
        byte[] ev = new byte[24 * nint.Size];
        XEventBytes.WriteLong(ev, 4, 0x1234);
        XEventBytes.WriteLong(ev, 5, -2);
        XEventBytes.WriteULong(ev, 8, nuint.MaxValue);

        XEventBytes.ReadLong(ev, 4).ShouldBe(0x1234);
        XEventBytes.ReadLong(ev, 5).ShouldBe(-2);
        XEventBytes.ReadULong(ev, 8).ShouldBe(nuint.MaxValue);
        XEventBytes.ReadLong(ev, 6).ShouldBe(0);
        XEventBytes.ReadLong(ev, 7).ShouldBe(0);

        // An int member (PropertyNotify's state) is read from the start of its slot.
        XEventBytes.WriteLong(ev, 7, 1);
        XEventBytes.ReadInt(ev, 7).ShouldBe(1);
    }

    [Fact]
    public void Format_32_property_data_is_one_c_long_per_item()
    {
        byte[] data = XEventBytes.Longs([1, 2, 0x7fffffff]);

        data.Length.ShouldBe(3 * nint.Size);
        BitConverter.ToInt32(data, nint.Size).ShouldBe(2);
        BitConverter.ToInt32(data, 2 * nint.Size).ShouldBe(0x7fffffff);
    }
}
