using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.FileTransfer;
using DeskPair.Core.Session;
using DeskPair.Protocol;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Tests;

/// <summary>Two engines wired back to back through in-memory queues, no network.</summary>
public class FileTransferEngineTests : IAsyncDisposable
{
    private readonly InMemoryFileSystem _fsA = new();
    private readonly InMemoryFileSystem _fsB = new();
    private readonly Channel<Message> _aToB = Channel.CreateUnbounded<Message>();
    private readonly Channel<Message> _bToA = Channel.CreateUnbounded<Message>();
    private readonly FileTransferEngine _a;
    private readonly FileTransferEngine _b;
    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));
    private readonly Task _pumpA;
    private readonly Task _pumpB;
    private readonly List<TransferJobSnapshot> _progressA = [];
    private int _bulkDelayMs;

    public FileTransferEngineTests()
    {
        _a = new FileTransferEngine(_fsA, (m, _, ct) => _aToB.Writer.WriteAsync(m, ct), NullLogger.Instance);
        _b = new FileTransferEngine(_fsB, async (m, p, ct) =>
        {
            if (p == MessagePriority.Bulk && _bulkDelayMs > 0)
            {
                await Task.Delay(_bulkDelayMs, ct); // emulate a slow link so cancellation lands mid-transfer
            }

            await _bToA.Writer.WriteAsync(m, ct);
        }, NullLogger.Instance);
        _a.Progress += s =>
        {
            lock (_progressA)
            {
                _progressA.Add(s);
            }
        };
        _pumpA = Task.Run(async () =>
        {
            await foreach (Message m in _bToA.Reader.ReadAllAsync(_cts.Token))
            {
                await _a.HandleAsync(m, _cts.Token);
            }
        });
        _pumpB = Task.Run(async () =>
        {
            await foreach (Message m in _aToB.Reader.ReadAllAsync(_cts.Token))
            {
                await _b.HandleAsync(m, _cts.Token);
            }
        });
    }

    private static byte[] Random(int size)
    {
        byte[] b = new byte[size];
        RandomNumberGenerator.Fill(b);
        return b;
    }

    [Fact]
    public async Task Lists_remote_directories()
    {
        _fsB.Write("root/docs/a.txt", "aaa"u8.ToArray());
        _fsB.Write("root/docs/.hidden", "h"u8.ToArray());
        _fsB.Write("root/pic.png", Random(10));

        FileDirectory dir = await _a.ListDirectoryAsync("root", false, _cts.Token);
        dir.Entries.Select(e => (e.Name, e.Type)).ShouldBe([("docs", FileType.FtDir), ("pic.png", FileType.FtFile)]);
        FileDirectory docs = await _a.ListDirectoryAsync("root/docs", true, _cts.Token);
        docs.Entries.Select(e => e.Name).ShouldBe([".hidden", "a.txt"]);
    }

    [Fact]
    public async Task Downloads_a_directory_tree_block_by_block()
    {
        byte[] big = Random(ProtocolConstants.FileBlockBytes * 3 + 777);
        _fsB.Write("share/big.bin", big, DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        _fsB.Write("share/sub/small.txt", "hello"u8.ToArray());
        _fsB.Write("share/empty.bin", []);

        int id = await _a.StartDownloadAsync("share", "downloads", false, _cts.Token);
        await _a.WaitForJobAsync(id, _cts.Token);

        _fsA.Read("downloads/big.bin").ShouldBe(big);
        _fsA.Read("downloads/sub/small.txt").ShouldBe("hello"u8.ToArray());
        _fsA.Read("downloads/empty.bin").ShouldBeEmpty();
        _fsA.Stat("downloads/big.bin")!.Modified.ToUnixTimeSeconds().ShouldBe(1_700_000_000);
        _fsA.Files.ShouldNotContain(f => f.EndsWith(FileTransferEngine.PartialSuffix));
        TransferJobSnapshot last;
        lock (_progressA)
        {
            last = _progressA.Last(s => s.Id == id);
        }

        last.State.ShouldBe(TransferState.Done);
        last.TransferredBytes.ShouldBe(big.Length + 5);
        last.TotalFiles.ShouldBe(3);
    }

    [Fact]
    public async Task Uploads_a_single_file()
    {
        byte[] data = Random(ProtocolConstants.FileBlockBytes + 1);
        _fsA.Write("local/report.pdf", data, DateTimeOffset.FromUnixTimeSeconds(1_600_000_000));

        int id = await _a.StartUploadAsync("local/report.pdf", "incoming", false, _cts.Token);
        await _a.WaitForJobAsync(id, _cts.Token);

        TransferJobSnapshot final;
        lock (_progressA)
        {
            final = _progressA.Last(s => s.Id == id);
        }

        final.State.ShouldBe(TransferState.Done, $"error={final.Error} files={final.TotalFiles} bytes={final.TransferredBytes}/{final.TotalBytes}");
        _fsB.Read("incoming/report.pdf").ShouldBe(data);
        _fsB.Stat("incoming/report.pdf")!.Modified.ToUnixTimeSeconds().ShouldBe(1_600_000_000);
    }

    [Fact]
    public async Task Identical_files_are_skipped_and_partial_files_resumed()
    {
        byte[] same = Random(2000);
        var mtime = DateTimeOffset.FromUnixTimeSeconds(1_650_000_000);
        _fsB.Write("src/same.bin", same, mtime);
        _fsA.Write("dst/same.bin", same, mtime);

        byte[] big = Random(ProtocolConstants.FileBlockBytes * 4);
        _fsB.Write("src/big.bin", big, mtime);
        _fsA.Write("dst/big.bin" + FileTransferEngine.PartialSuffix, big[..(ProtocolConstants.FileBlockBytes * 2 + 100)]);

        int id = await _a.StartDownloadAsync("src", "dst", false, _cts.Token);
        await _a.WaitForJobAsync(id, _cts.Token);

        _fsA.Read("dst/big.bin").ShouldBe(big);
        _fsA.Read("dst/same.bin").ShouldBe(same);
        TransferJobSnapshot last;
        lock (_progressA)
        {
            last = _progressA.Last(s => s.Id == id);
        }

        last.State.ShouldBe(TransferState.Done);
        // Only the resumed tail of big.bin crossed the wire.
        last.TransferredBytes.ShouldBe(same.Length + big.Length);
    }

    [Fact]
    public async Task Cancel_stops_the_sender_and_removes_the_partial_file()
    {
        _fsB.Write("src/huge.bin", Random(ProtocolConstants.FileBlockBytes * 200));
        _bulkDelayMs = 2;
        var gate = new SemaphoreSlim(0);
        _a.Progress += s =>
        {
            if (s.TransferredBytes > ProtocolConstants.FileBlockBytes * 10)
            {
                gate.Release();
            }
        };

        int id = await _a.StartDownloadAsync("src", "dst", false, _cts.Token);
        await gate.WaitAsync(_cts.Token);
        await _a.CancelAsync(id, _cts.Token);

        // A cancelled job is not a finished one, and the waiter is told so.
        await Should.ThrowAsync<OperationCanceledException>(async () => await _a.WaitForJobAsync(id, _cts.Token));

        _fsA.Files.ShouldBeEmpty();
        TransferJobSnapshot last;
        lock (_progressA)
        {
            last = _progressA.Last(s => s.Id == id);
        }

        last.State.ShouldBe(TransferState.Cancelled);
        await Task.Delay(100);
        _b.Jobs.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_listing_faults_instead_of_hanging()
    {
        // When the host refuses a directory (e.g. outside a file-transfer allow-list) it answers with an
        // error carrying the listing's id. The requester must surface that as a fault, not wait forever for a
        // directory response that will never come. If this regressed, the await would hang until the 30 s test
        // token cancels and throw OperationCanceledException instead of the IOException asserted here.
        _b.IsPathAllowed = _ => false;
        _fsB.Write("blocked/secret.bin", Random(64));

        await Should.ThrowAsync<IOException>(async () => await _a.ListDirectoryAsync("blocked", false, _cts.Token));
    }

    [Fact]
    public async Task Path_traversal_in_upload_is_rejected()
    {
        _fsA.Write("evil/../etc/passwd", "x"u8.ToArray());
        int id = await _a.StartUploadAsync("evil", "target", false, _cts.Token);
        await _a.WaitForJobAsync(id, _cts.Token);
        _fsB.Files.ShouldBeEmpty();
    }

    [Fact]
    public async Task All_files_under_a_directory_can_be_listed_in_one_request()
    {
        _fsB.Write("tree/a.txt", Random(8));
        _fsB.Write("tree/nested/b.txt", Random(8));
        _fsB.Write("tree/nested/deeper/c.txt", Random(8));

        FileDirectory shallow = await _a.ListDirectoryAsync("tree", false, _cts.Token);
        FileDirectory all = await _a.ListAllFilesAsync("tree", false, _cts.Token);

        // The shallow listing is what a copy records; the recursive one is what a paste expands it into.
        shallow.Entries.Count(e => e.Type == FileType.FtFile).ShouldBe(1);
        all.Entries.Where(e => e.Type == FileType.FtFile).Select(e => e.Name.Replace('\\', '/'))
           .ShouldBe(["a.txt", "nested/b.txt", "nested/deeper/c.txt"], ignoreOrder: true);
    }

    [Fact]
    public async Task A_promised_file_is_fetched_only_when_it_is_asked_for()
    {
        _fsB.Write("desktop/report.pdf", Random(4096));
        string stagingRoot = Path.Combine(Path.GetTempPath(), "DeskPair.Tests", Guid.NewGuid().ToString("N"));
        var staging = new Clipboard.ClipboardStaging(NullLogger.Instance, stagingRoot);
        try
        {
            var listing = new Platform.Abstractions.Clipboard.FilePromiseListing("tok", "desktop", [
                new Platform.Abstractions.Clipboard.FilePromiseEntry(1, "report.pdf", 4096, DateTimeOffset.UnixEpoch, IsDirectory: false),
            ]);
            var source = new Clipboard.EngineFilePromiseSource(_a, listing, staging, NullLogger.Instance);

            _fsA.Files.ShouldBeEmpty("nothing may be fetched until something pastes");

            string destination = source.StagedPathFor(1);
            await source.FetchAsync(1, destination, written: null, _cts.Token);

            _fsA.Files.Count().ShouldBe(1);
            _fsA.Files.Single().ShouldEndWith("report.pdf");
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Remote_directory_operations_round_trip()
    {
        _fsB.Write("root/old.txt", "x"u8.ToArray());
        await _a.CreateDirectoryAsync("root/new-dir", _cts.Token);
        await _a.RenameAsync("root/old.txt", "renamed.txt", _cts.Token);
        await Task.Delay(100);
        _fsB.Files.ShouldContain("root/renamed.txt");
        _fsB.Stat("root/new-dir")!.IsDirectory.ShouldBeTrue();

        await _a.RemoveFileAsync("root/renamed.txt", _cts.Token);
        await _a.RemoveDirectoryAsync("root/new-dir", false, _cts.Token);
        await Task.Delay(100);
        _fsB.Files.ShouldBeEmpty();
        _fsB.Stat("root/new-dir").ShouldBeNull();
    }

    [Fact]
    public async Task Peer_access_can_be_restricted()
    {
        _fsB.Write("private/secret.txt", "s"u8.ToArray());
        _b.IsPathAllowed = p => !p.StartsWith("private", StringComparison.Ordinal);
        (int Job, string Path, string Error) reported = default;
        _a.RemoteError += (job, path, error) => reported = (job, path, error);

        int id = await _a.StartDownloadAsync("private", "dst", false, _cts.Token);

        // A refused job must be distinguishable from a finished one: before, this awaited quietly and the
        // caller could not tell the difference.
        await Should.ThrowAsync<FileTransferFailedException>(async () => await _a.WaitForJobAsync(id, _cts.Token));
        reported.Job.ShouldBe(id);
        reported.Path.ShouldBe("private");
        reported.Error.ShouldNotBeNullOrEmpty();
        _fsA.Files.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/../../x")]
    [InlineData("/abs")]
    [InlineData("C:\\abs")]
    [InlineData("CON")]
    [InlineData("a/nul.txt")]
    [InlineData("bad<name>")]
    [InlineData("trailing.")]
    public void Path_guard_rejects_escapes(string relative)
    {
        Should.Throw<PathGuardException>(() => PathGuard.ValidateRelative(relative));
    }

    [Theory]
    [InlineData("a.txt")]
    [InlineData("dir/sub/file.name.ext")]
    [InlineData("dir\\win\\style.txt")]
    [InlineData("中文/檔案.txt")]
    public void Path_guard_accepts_normal_names(string relative)
    {
        PathGuard.ValidateRelative(relative).Length.ShouldBeGreaterThan(0);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _aToB.Writer.TryComplete();
        _bToA.Writer.TryComplete();
        await _a.DisposeAsync();
        await _b.DisposeAsync();
        try
        {
            await Task.WhenAll(_pumpA, _pumpB);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
