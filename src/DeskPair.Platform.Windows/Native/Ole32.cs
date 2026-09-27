using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DeskPair.Platform.Windows.Native;

/// <summary>Blittable by hand: the ComTypes versions are not, because their STGMEDIUM holds an object.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FORMATETC
{
    public ushort cfFormat;
    public nint ptd;
    public uint dwAspect;
    public int lindex;
    public uint tymed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STGMEDIUM
{
    public uint tymed;
    public nint unionmember;
    public nint pUnkForRelease;
}

[StructLayout(LayoutKind.Sequential)]
internal struct STATSTG
{
    public nint pwcsName;
    public uint type;
    public ulong cbSize;
    public long mtime;
    public long ctime;
    public long atime;
    public uint grfMode;
    public uint grfLocksSupported;
    public Guid clsid;
    public uint grfStateBits;
    public uint reserved;
}

/// <summary>
/// MIDL declaration order, which is vtable order. A method out of sequence is a silent call through the wrong
/// slot and no test catches it, so this order must not be changed without re-reading objidl.idl:
///
///   GetData, GetDataHere, QueryGetData, GetCanonicalFormatEtc, SetData, EnumFormatEtc,
///   DAdvise, DUnadvise, EnumDAdvise
/// </summary>
[GeneratedComInterface]
[Guid("0000010E-0000-0000-C000-000000000046")]
internal partial interface IDataObjectNative
{
    [PreserveSig] int GetData(ref FORMATETC format, out STGMEDIUM medium);
    [PreserveSig] int GetDataHere(ref FORMATETC format, ref STGMEDIUM medium);
    [PreserveSig] int QueryGetData(ref FORMATETC format);
    [PreserveSig] int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut);
    [PreserveSig] int SetData(ref FORMATETC format, ref STGMEDIUM medium, [MarshalAs(UnmanagedType.Bool)] bool release);
    [PreserveSig] int EnumFormatEtc(uint direction, out nint enumerator);
    [PreserveSig] int DAdvise(ref FORMATETC format, uint advf, nint sink, out uint connection);
    [PreserveSig] int DUnadvise(uint connection);
    [PreserveSig] int EnumDAdvise(out nint enumerator);
}

/// <summary>ISequentialStream's two methods first, then IStream's eleven.</summary>
[GeneratedComInterface]
[Guid("0000000C-0000-0000-C000-000000000046")]
internal partial interface IStreamNative
{
    [PreserveSig] int Read(nint pv, uint cb, nint pcbRead);
    [PreserveSig] int Write(nint pv, uint cb, nint pcbWritten);
    [PreserveSig] int Seek(long move, uint origin, nint newPosition);
    [PreserveSig] int SetSize(ulong size);
    [PreserveSig] int CopyTo(nint destination, ulong cb, nint read, nint written);
    [PreserveSig] int Commit(uint flags);
    [PreserveSig] int Revert();
    [PreserveSig] int LockRegion(ulong offset, ulong cb, uint type);
    [PreserveSig] int UnlockRegion(ulong offset, ulong cb, uint type);

    // STATSTG crosses as a raw pointer: the source generator refuses the struct itself (SYSLIB1051), and we
    // want to own the CoTaskMemAlloc'd pwcsName anyway.
    [PreserveSig] int Stat(nint stat, uint flag);
    [PreserveSig] int Clone(out nint clone);
}

internal static partial class Ole32
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int DV_E_FORMATETC = unchecked((int)0x80040064);
    public const int DV_E_LINDEX = unchecked((int)0x80040068);
    public const int DV_E_TYMED = unchecked((int)0x80040069);
    public const int DV_E_DVASPECT = unchecked((int)0x8004006B);
    public const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    public const int DATA_S_SAMEFORMATETC = 0x00040130;
    public const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);
    public const int STG_E_INVALIDPOINTER = unchecked((int)0x80030009);
    public const int STG_E_READFAULT = unchecked((int)0x8003001E);
    public const int RPC_S_CALLPENDING = unchecked((int)0x80010115);

    public const uint TYMED_HGLOBAL = 1;
    public const uint TYMED_ISTREAM = 4;
    public const uint DVASPECT_CONTENT = 1;
    public const uint DATADIR_GET = 1;
    public const uint STGTY_STREAM = 2;

    public const uint COWAIT_DISPATCH_CALLS = 0x2;
    public const uint COWAIT_DISPATCH_WINDOW_MESSAGES = 0x4;

    public static readonly Guid IID_IDataObject = new("0000010E-0000-0000-C000-000000000046");

    [LibraryImport("ole32.dll")]
    public static partial int OleInitialize(nint reserved);

    [LibraryImport("ole32.dll")]
    public static partial void OleUninitialize();

    /// <summary>
    /// Takes an <c>IDataObject*</c>, and means it: ole32 does not QueryInterface, it calls straight through
    /// the vtable it is handed. Given a ComWrappers IUnknown it indexes IDataObject's slots into a
    /// three-entry vtable and fails with a bare E_FAIL without ever entering managed code.
    /// </summary>
    [LibraryImport("ole32.dll")]
    public static partial int OleSetClipboard(nint dataObject);

    [LibraryImport("ole32.dll")]
    public static partial int OleGetClipboard(out nint dataObject);

    [LibraryImport("ole32.dll")]
    public static partial int OleIsCurrentClipboard(nint dataObject);

    /// <summary>
    /// Waits while still dispatching incoming COM calls and window messages. A plain wait on the thread that
    /// owns the data object stops the message loop that services the very call it is inside.
    /// </summary>
    [LibraryImport("ole32.dll")]
    public static partial int CoWaitForMultipleHandles(uint flags, uint timeoutMs, uint count, [In] nint[] handles, out uint index);

    [LibraryImport("shell32.dll")]
    public static partial int SHCreateStdEnumFmtEtc(uint cfmt, [In] FORMATETC[] afmt, out nint ppenum);
}
