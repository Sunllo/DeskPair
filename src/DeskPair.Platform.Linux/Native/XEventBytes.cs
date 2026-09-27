using System.Runtime.InteropServices;

namespace DeskPair.Platform.Linux.Native;

/// <summary>
/// XEvent members and format-32 property data as bytes.
///
/// An XEvent is a union padded to 24 C longs, and every member after the leading <c>int type</c> is a C long, a
/// pointer or an int that starts a long-sized slot. So member n of any event sits at n * sizeof(long), and on Linux
/// a C long is the pointer size: 8 bytes on x86-64 and aarch64, 4 on 32-bit ARM. Counting members rather than
/// bytes is what keeps the clipboard right on both.
/// </summary>
internal static class XEventBytes
{
    public static int At(int member) => member * nint.Size;

    public static nint ReadLong(byte[] ev, int member) => MemoryMarshal.Read<nint>(ev.AsSpan(At(member)));

    public static nuint ReadULong(byte[] ev, int member) => MemoryMarshal.Read<nuint>(ev.AsSpan(At(member)));

    public static int ReadInt(byte[] ev, int member) => MemoryMarshal.Read<int>(ev.AsSpan(At(member)));

    public static void WriteLong(byte[] ev, int member, nint value) => MemoryMarshal.Write(ev.AsSpan(At(member)), in value);

    public static void WriteULong(byte[] ev, int member, nuint value) => MemoryMarshal.Write(ev.AsSpan(At(member)), in value);

    /// <summary>Format-32 property data, which Xlib takes as an array of C longs whatever the format number says.</summary>
    public static byte[] Longs(ReadOnlySpan<nint> values) => MemoryMarshal.AsBytes(values).ToArray();
}
