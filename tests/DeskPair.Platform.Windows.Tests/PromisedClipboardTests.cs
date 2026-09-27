using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Platform.Windows.Clipboard;
using DeskPair.Platform.Windows.Native;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// A source whose files are produced by a test rather than by a session, and slowly, so the stream that
/// tails a download is exercised against a file that is still growing.
/// </summary>
internal sealed class ScriptedPromiseSource : IFilePromiseSource
{
    private readonly string _root;
    private readonly Dictionary<long, (string Relative, byte[] Bytes)> _files;

    public ScriptedPromiseSource(string root, params (long Id, string Relative, byte[] Bytes)[] files)
    {
        _root = root;
        _files = files.ToDictionary(f => f.Id, f => (f.Relative, f.Bytes));
        Directory.CreateDirectory(root);
    }

    /// <summary>Bytes written per step; a fetch pauses between steps so a reader sees a partial file.</summary>
    public int ChunkBytes { get; set; } = int.MaxValue;

    public TimeSpan ChunkDelay { get; set; } = TimeSpan.Zero;

    public Exception? FailWith { get; set; }

    public int Fetches;

    public Task<IReadOnlyList<FilePromiseEntry>> ResolveAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FilePromiseEntry>>(
            [.. _files.Select(f => new FilePromiseEntry(
                f.Key,
                f.Value.Relative,
                f.Value.Bytes.Length,
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
                IsDirectory: false))]);

    public string StagedPathFor(long id)
    {
        string path = Path.Combine(_root, _files[id].Relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    public async Task FetchAsync(long id, string destinationPath, IProgress<long>? written, CancellationToken ct)
    {
        Interlocked.Increment(ref Fetches);
        if (FailWith is not null)
        {
            throw FailWith;
        }

        byte[] bytes = _files[id].Bytes;
        await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        for (int offset = 0; offset < bytes.Length; offset += ChunkBytes)
        {
            int n = Math.Min(ChunkBytes, bytes.Length - offset);
            await file.WriteAsync(bytes.AsMemory(offset, n), ct);
            await file.FlushAsync(ct);
            written?.Report(offset + n);
            if (ChunkDelay > TimeSpan.Zero && offset + n < bytes.Length)
            {
                await Task.Delay(ChunkDelay, ct);
            }
        }
    }
}

public sealed class PromisedDataObjectTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeskPair.Tests", Guid.NewGuid().ToString("N"));

    private static readonly uint CfDescriptor = User32.RegisterClipboardFormatW("FileGroupDescriptorW");
    private static readonly uint CfContents = User32.RegisterClipboardFormatW("FileContents");

    [Fact]
    public void The_descriptor_has_the_layout_explorer_expects()
    {
        var source = new ScriptedPromiseSource(
            _root,
            (1, "notes.txt", "hello"u8.ToArray()),
            (2, "pictures/holiday.jpg", new byte[5_000_000_000 % int.MaxValue]));
        using var data = new PromisedDataObject(source, [], NullLogger.Instance);

        byte[] descriptor = GetGlobal(data, CfDescriptor);

        // FILEGROUPDESCRIPTORW is a count followed by 592-byte entries; anything else and Explorer reads
        // one file's fields out of another's.
        descriptor.Length.ShouldBe(4 + (592 * 2));
        BitConverter.ToUInt32(descriptor, 0).ShouldBe(2u);

        ReadOnlySpan<byte> second = descriptor.AsSpan(4 + 592);
        string name = Encoding.Unicode.GetString(second.Slice(72, 520)).TrimEnd('\0');
        name.ShouldBe("pictures\\holiday.jpg", "the path must be relative and use backslashes");

        long size = ((long)BitConverter.ToUInt32(second[64..]) << 32) | BitConverter.ToUInt32(second[68..]);
        size.ShouldBe(5_000_000_000L % int.MaxValue, "the size drives Explorer's progress dialog");
    }

    [Fact]
    public void A_file_is_fetched_only_when_its_contents_are_asked_for()
    {
        var source = new ScriptedPromiseSource(_root, (1, "a.txt", "x"u8.ToArray()), (2, "b.txt", "y"u8.ToArray()));
        using var data = new PromisedDataObject(source, [], NullLogger.Instance);

        GetGlobal(data, CfDescriptor);
        source.Fetches.ShouldBe(0, "reading the listing must not download anything");

        using (ReadContents(data, 0, out string text))
        {
            text.ShouldBe("x");
        }

        source.Fetches.ShouldBe(1, "only the file that was asked for");
    }

    [Fact]
    public void A_stream_follows_a_file_that_is_still_being_written()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('a', 40_000));
        var source = new ScriptedPromiseSource(_root, (1, "big.bin", payload))
        {
            ChunkBytes = 4_000,
            ChunkDelay = TimeSpan.FromMilliseconds(20),
        };
        using var data = new PromisedDataObject(source, [], NullLogger.Instance);

        using IDisposable _ = ReadContents(data, 0, out string text);

        // The reader outruns the writer by design; it must wait rather than report a short file.
        text.Length.ShouldBe(payload.Length);
    }

    [Fact]
    public void A_failed_transfer_fails_the_read_rather_than_truncating_it()
    {
        var source = new ScriptedPromiseSource(_root, (1, "a.txt", "content"u8.ToArray()))
        {
            FailWith = new IOException("the peer refused"),
        };
        using var data = new PromisedDataObject(source, [], NullLogger.Instance);

        var format = new FORMATETC { cfFormat = (ushort)CfContents, dwAspect = 1, lindex = 0, tymed = 4 };
        data.GetData(ref format, out STGMEDIUM medium).ShouldBe(0);

        var stream = (IStreamNative)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(medium.unionmember, CreateObjectFlags.UniqueInstance);
        byte[] buffer = new byte[16];
        int hr;
        unsafe
        {
            fixed (byte* p = buffer)
            {
                int read;
                hr = stream.Read((nint)p, (uint)buffer.Length, (nint)(&read));
            }
        }

        hr.ShouldNotBe(0, "a caller that got S_OK and zero bytes would write an empty file and call it done");
        Marshal.Release(medium.unionmember);
    }

    [Fact]
    public void An_index_past_the_end_is_refused()
    {
        var source = new ScriptedPromiseSource(_root, (1, "a.txt", "x"u8.ToArray()));
        using var data = new PromisedDataObject(source, [], NullLogger.Instance);

        var format = new FORMATETC { cfFormat = (ushort)CfContents, dwAspect = 1, lindex = 7, tymed = 4 };

        data.GetData(ref format, out _).ShouldBe(unchecked((int)0x80040068), "DV_E_LINDEX");
    }

    [Fact]
    public void The_probe_index_minus_one_is_accepted()
    {
        // OLE walks EnumFormatEtc with lindex -1 before publishing. Refusing it makes OleSetClipboard fail
        // with a bare E_FAIL and nothing to go on.
        var source = new ScriptedPromiseSource(_root, (1, "a.txt", "x"u8.ToArray()));
        using var data = new PromisedDataObject(source, [], NullLogger.Instance);

        var format = new FORMATETC { cfFormat = (ushort)CfContents, dwAspect = 1, lindex = -1, tymed = 4 };

        data.QueryGetData(ref format).ShouldBe(0);
    }

    [Fact]
    public void Text_copied_alongside_the_files_is_still_offered()
    {
        var source = new ScriptedPromiseSource(_root, (1, "a.txt", "x"u8.ToArray()));
        using var data = new PromisedDataObject(
            source,
            [new ClipboardItem(ClipboardItemFormat.Text, "beside the files"u8.ToArray())],
            NullLogger.Instance);

        byte[] text = GetGlobal(data, User32.CF_UNICODETEXT);

        Encoding.Unicode.GetString(text).TrimEnd('\0').ShouldBe("beside the files");
    }

    private static byte[] GetGlobal(PromisedDataObject data, uint format)
    {
        var etc = new FORMATETC { cfFormat = (ushort)format, dwAspect = 1, lindex = -1, tymed = 1 };
        data.GetData(ref etc, out STGMEDIUM medium).ShouldBe(0);

        nint p = Kernel32.GlobalLock(medium.unionmember);
        byte[] bytes = new byte[(int)Kernel32.GlobalSize(medium.unionmember)];
        Marshal.Copy(p, bytes, 0, bytes.Length);
        Kernel32.GlobalUnlock(medium.unionmember);
        Kernel32.GlobalFree(medium.unionmember);
        return bytes;
    }

    private static IDisposable ReadContents(PromisedDataObject data, int index, out string text)
    {
        var format = new FORMATETC { cfFormat = (ushort)CfContents, dwAspect = 1, lindex = index, tymed = 4 };
        data.GetData(ref format, out STGMEDIUM medium).ShouldBe(0);
        medium.tymed.ShouldBe(4u);

        var stream = (IStreamNative)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(medium.unionmember, CreateObjectFlags.UniqueInstance);
        var sink = new MemoryStream();
        byte[] buffer = new byte[8192];
        unsafe
        {
            fixed (byte* p = buffer)
            {
                int read;
                do
                {
                    stream.Read((nint)p, (uint)buffer.Length, (nint)(&read)).ShouldBe(0);
                    sink.Write(buffer, 0, read);
                }
                while (read > 0);
            }
        }

        text = Encoding.UTF8.GetString(sink.ToArray());
        nint pointer = medium.unionmember;
        return new Releaser(pointer);
    }

    private sealed class Releaser(nint pointer) : IDisposable
    {
        public void Dispose() => Marshal.Release(pointer);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>
/// The whole Windows path with a real clipboard: publish a promise through OleSetClipboard and read it back
/// through OleGetClipboard, which is what Explorer does. Nothing below is exercised by the unit tests above —
/// the vtable, the IDataObject pointer OleSetClipboard insists on, and the pumping wait on the OLE thread
/// that lets a cross-apartment call reach the object at all.
/// </summary>
[Collection("clipboard")]
public sealed class OleClipboardRoundTripTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeskPair.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task A_promise_published_to_the_clipboard_can_be_read_back_and_drained()
    {
        // A skipped test reports as passed, so say out loud which one this was.
        Console.WriteLine($"InteractiveDesktop.IsAvailable = {InteractiveDesktop.IsAvailable}");
        if (!InteractiveDesktop.IsAvailable)
        {
            return; // OLE cannot take the clipboard on a disconnected session
        }

        byte[] payload = Encoding.UTF8.GetBytes(new string('z', 20_000));
        var source = new ScriptedPromiseSource(_root, (1, "report.bin", payload)) { ChunkBytes = 3_000, ChunkDelay = TimeSpan.FromMilliseconds(5) };
        var listing = new FilePromiseListing("tok", "/remote", [
            new FilePromiseEntry(1, "report.bin", payload.Length, DateTimeOffset.UnixEpoch, IsDirectory: false),
        ]);

        await using var clipboard = new WindowsClipboard(NullLogger.Instance);
        clipboard.CanPromiseFiles.ShouldBeTrue();

        await clipboard.WriteWithPromiseAsync(
            [new ClipboardItem(ClipboardItemFormat.Text, "beside the file"u8.ToArray())],
            listing,
            source,
            CancellationToken.None);

        // Everything below runs on its own STA thread, as a pasting application's would.
        string? failure = null;
        int descriptorCount = 0;
        string text = string.Empty;
        int drained = 0;

        var reader = new Thread(() =>
        {
            try
            {
                Ole32.OleInitialize(0).ShouldBeOneOf(0, 1);
                Ole32.OleGetClipboard(out nint raw).ShouldBe(0);

                var data = (IDataObjectNative)new StrategyBasedComWrappers()
                    .GetOrCreateObjectForComInstance(raw, CreateObjectFlags.UniqueInstance);

                uint cfDescriptor = User32.RegisterClipboardFormatW("FileGroupDescriptorW");
                uint cfContents = User32.RegisterClipboardFormatW("FileContents");

                var want = new FORMATETC { cfFormat = (ushort)cfDescriptor, dwAspect = 1, lindex = -1, tymed = 1 };
                data.GetData(ref want, out STGMEDIUM descriptor).ShouldBe(0);
                nint locked = Kernel32.GlobalLock(descriptor.unionmember);
                descriptorCount = Marshal.ReadInt32(locked);
                Kernel32.GlobalUnlock(descriptor.unionmember);
                Kernel32.GlobalFree(descriptor.unionmember);

                var contents = new FORMATETC { cfFormat = (ushort)cfContents, dwAspect = 1, lindex = 0, tymed = 4 };
                data.GetData(ref contents, out STGMEDIUM stm).ShouldBe(0);
                var stream = (IStreamNative)new StrategyBasedComWrappers()
                    .GetOrCreateObjectForComInstance(stm.unionmember, CreateObjectFlags.UniqueInstance);

                byte[] buffer = new byte[4096];
                unsafe
                {
                    fixed (byte* p = buffer)
                    {
                        int read;
                        do
                        {
                            stream.Read((nint)p, (uint)buffer.Length, (nint)(&read)).ShouldBe(0);
                            drained += read;
                        }
                        while (read > 0);
                    }
                }

                Marshal.Release(stm.unionmember);

                var textFormat = new FORMATETC { cfFormat = (ushort)User32.CF_UNICODETEXT, dwAspect = 1, lindex = -1, tymed = 1 };
                data.GetData(ref textFormat, out STGMEDIUM t).ShouldBe(0);
                nint tp = Kernel32.GlobalLock(t.unionmember);
                text = Marshal.PtrToStringUni(tp) ?? string.Empty;
                Kernel32.GlobalUnlock(t.unionmember);
                Kernel32.GlobalFree(t.unionmember);

                Marshal.Release(raw);
            }
            catch (Exception e)
            {
                failure = e.ToString();
            }
        });

        reader.SetApartmentState(ApartmentState.STA);
        reader.Start();
        reader.Join(TimeSpan.FromSeconds(60)).ShouldBeTrue("the paste must not hang");

        failure.ShouldBeNull();
        descriptorCount.ShouldBe(1);
        drained.ShouldBe(payload.Length);
        text.ShouldBe("beside the file");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
