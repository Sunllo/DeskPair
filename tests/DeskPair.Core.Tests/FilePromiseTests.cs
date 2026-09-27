using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Core.Clipboard;
using DeskPair.Core.FileTransfer;
using DeskPair.Core.Testing;
using DeskPair.Platform.Abstractions.Clipboard;

namespace DeskPair.Core.Tests;

public sealed class FilePromiseCodecTests
{
    private static FilePromiseListing Listing(params FilePromiseEntry[] entries) =>
        new("token-1", "C:\\Users\\someone\\Desktop", entries);

    private static FilePromiseEntry Entry(long id, string path, long size = 10) =>
        new(id, path, size, DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), IsDirectory: false);

    [Fact]
    public void A_listing_survives_the_round_trip()
    {
        FilePromiseListing original = Listing(
            Entry(1, "notes.txt", 42),
            Entry(2, "pictures/holiday.jpg", 3_000_000),
            new FilePromiseEntry(3, "pictures", 0, DateTimeOffset.UnixEpoch, IsDirectory: true));

        ClipboardItem item = FilePromiseCodec.Encode(original);
        item.Format.ShouldBe(ClipboardItemFormat.FileList);

        FilePromiseListing? decoded = FilePromiseCodec.Decode(item.Payload, out int rejected);

        rejected.ShouldBe(0);
        decoded.ShouldNotBeNull();
        decoded.Token.ShouldBe("token-1");
        decoded.RemoteRoot.ShouldBe("C:\\Users\\someone\\Desktop");
        decoded.Entries.Count.ShouldBe(3);
        decoded.Entries[1].Id.ShouldBe(2);
        decoded.Entries[1].RelativePath.ShouldBe("pictures/holiday.jpg");
        decoded.Entries[1].Size.ShouldBe(3_000_000);
        decoded.Entries[1].Modified.ShouldBe(original.Entries[1].Modified);
        decoded.Entries[2].IsDirectory.ShouldBeTrue();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\system32\\cmd.exe")]
    [InlineData("CON")]
    [InlineData("trailing.")]
    [InlineData("")]
    public void A_name_that_escapes_is_dropped_rather_than_trusted(string relative)
    {
        // Every one of these arrives from the other machine. The listing still decodes; the entry does not.
        ClipboardItem item = FilePromiseCodec.Encode(Listing(Entry(1, "safe.txt"), Entry(2, relative)));

        FilePromiseListing? decoded = FilePromiseCodec.Decode(item.Payload, out int rejected);

        rejected.ShouldBe(1);
        decoded!.Entries.Count.ShouldBe(1);
        decoded.Entries[0].RelativePath.ShouldBe("safe.txt");
    }

    [Fact]
    public void A_name_too_long_for_a_windows_descriptor_is_refused_not_truncated()
    {
        string tooLong = new('a', FilePromiseCodec.MaxRelativePathLength + 1);
        FilePromiseCodec.IsAcceptable(tooLong).ShouldBeFalse();
        FilePromiseCodec.IsAcceptable(new string('a', FilePromiseCodec.MaxRelativePathLength)).ShouldBeTrue();
    }

    [Fact]
    public void A_payload_that_is_not_a_listing_decodes_to_nothing_rather_than_throwing()
    {
        FilePromiseCodec.Decode(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, out _).ShouldBeNull();
    }
}

public sealed class ClipboardPromiseRouterTests
{
    private readonly ClipboardStaging _staging = new(
        NullLogger.Instance,
        Path.Combine(Path.GetTempPath(), "DeskPair.Tests", Guid.NewGuid().ToString("N")));

    private ClipboardPromiseRouter Router() => new(_staging, NullLogger.Instance);

    private static IReadOnlyList<ClipboardItem> WithFiles() =>
    [
        new ClipboardItem(ClipboardItemFormat.Text, "some text"u8.ToArray()),
        FilePromiseCodec.Encode(new FilePromiseListing("t", "/home/someone", [
            new FilePromiseEntry(1, "a.txt", 5, DateTimeOffset.UnixEpoch, IsDirectory: false),
        ])),
    ];

    [Fact]
    public async Task Plain_content_goes_through_untouched()
    {
        var clipboard = new FakeClipboard();
        IReadOnlyList<ClipboardItem> items = [new ClipboardItem(ClipboardItemFormat.Text, "hello"u8.ToArray())];

        await Router().WriteAsync(clipboard, items, engine: null, filesAllowed: true, CancellationToken.None);

        clipboard.Text.ShouldBe("hello");
        clipboard.PromiseWrites.ShouldBe(0);
    }

    [Fact]
    public async Task A_clipboard_that_cannot_promise_still_gets_the_text()
    {
        var clipboard = new FakeClipboard { CanPromiseFiles = false };

        await Router().WriteAsync(clipboard, WithFiles(), engine: null, filesAllowed: true, CancellationToken.None);

        clipboard.Text.ShouldBe("some text");
        clipboard.PromiseWrites.ShouldBe(0);
        clipboard.Content.ShouldNotContain(i => i.Format == ClipboardItemFormat.FileList);
    }

    [Fact]
    public async Task Without_file_permission_nothing_is_promised()
    {
        var clipboard = new FakeClipboard();

        await Router().WriteAsync(clipboard, WithFiles(), engine: null, filesAllowed: false, CancellationToken.None);

        clipboard.PromiseWrites.ShouldBe(0);
        clipboard.Promised.ShouldBeNull();
        clipboard.Text.ShouldBe("some text");
    }

    [Fact]
    public async Task The_raw_listing_item_never_reaches_the_clipboard()
    {
        // It is an internal encoding, not something a user should be able to paste into a text editor.
        var clipboard = new FakeClipboard { CanPromiseFiles = false };

        await Router().WriteAsync(clipboard, WithFiles(), engine: null, filesAllowed: true, CancellationToken.None);

        clipboard.Content.ShouldAllBe(i => i.Format != ClipboardItemFormat.FileList);
    }
}

public sealed class ClipboardStagingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeskPair.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Nothing_exists_until_a_file_is_asked_for()
    {
        var staging = new ClipboardStaging(NullLogger.Instance, _root);

        Directory.Exists(_root).ShouldBeFalse("a promise nobody pastes must cost nothing");

        staging.PathFor("token", "a/b.txt");
        Directory.Exists(Path.Combine(_root, "token", "a")).ShouldBeTrue();
    }

    [Fact]
    public void A_path_that_escapes_its_promise_directory_is_refused()
    {
        var staging = new ClipboardStaging(NullLogger.Instance, _root);

        Should.Throw<InvalidOperationException>(() => staging.PathFor("token", "../../elsewhere.txt"));
    }

    [Fact]
    public void A_token_is_never_used_as_a_path_unexamined()
    {
        var staging = new ClipboardStaging(NullLogger.Instance, _root);

        string path = staging.PathFor("../../evil", "a.txt");

        path.ShouldStartWith(Path.GetFullPath(_root));
    }

    [Fact]
    public void Directories_from_an_earlier_run_are_swept()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var staging = new ClipboardStaging(NullLogger.Instance, _root, clock);
        staging.PathFor("old", "a.txt");
        staging.PathFor("new", "b.txt");

        Directory.SetLastWriteTimeUtc(Path.Combine(_root, "old"), DateTime.UtcNow - ClipboardStaging.StaleAfter - TimeSpan.FromHours(1));

        staging.SweepStale().ShouldBe(1);
        Directory.Exists(Path.Combine(_root, "old")).ShouldBeFalse();
        Directory.Exists(Path.Combine(_root, "new")).ShouldBeTrue();
    }

    [Fact]
    public void Discarding_a_promise_removes_what_it_fetched()
    {
        var staging = new ClipboardStaging(NullLogger.Instance, _root);
        File.WriteAllText(staging.PathFor("token", "a.txt"), "x");

        staging.Discard("token");

        Directory.Exists(Path.Combine(_root, "token")).ShouldBeFalse();
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

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class LocalFileOfferTests
{
    private readonly InMemoryFileSystem _fs = new();
    private readonly LocalFileOffer _offer = new(NullLogger.Instance);

    [Fact]
    public void Copied_files_become_a_listing_of_names_and_sizes()
    {
        _fs.Write("desktop/notes.txt", new byte[42]);
        _fs.Write("desktop/report.pdf", new byte[7]);

        ClipboardItem? item = _offer.Offer(["desktop/notes.txt", "desktop/report.pdf"], _fs);

        item.ShouldNotBeNull();
        item.Format.ShouldBe(ClipboardItemFormat.FileList);
        _offer.Current!.RemoteRoot.ShouldBe("desktop");
        _offer.Current.Entries.Select(e => e.RelativePath).ShouldBe(["notes.txt", "report.pdf"]);
        _offer.Current.Entries[0].Size.ShouldBe(42);
        _offer.Current.Entries.Select(e => e.Id).Distinct().Count().ShouldBe(2, "ids address files, so they must differ");
    }

    [Fact]
    public void Nothing_copied_means_nothing_offered_and_nothing_readable()
    {
        _offer.Offer([], _fs).ShouldBeNull();
        _offer.Current.ShouldBeNull();
        _offer.Allows("anything").ShouldBeFalse();
    }

    [Fact]
    public void The_peer_may_read_only_what_was_copied()
    {
        _fs.Write("desktop/shared.txt", new byte[1]);
        _fs.Write("secrets/passwords.txt", new byte[1]);
        _offer.Offer(["desktop/shared.txt"], _fs);

        _offer.Allows("desktop/shared.txt").ShouldBeTrue();
        _offer.Allows("secrets/passwords.txt").ShouldBeFalse();
        _offer.Allows("desktop").ShouldBeFalse("the folder was not copied, one file in it was");
    }

    [Fact]
    public void A_copied_folder_may_be_read_through()
    {
        _fs.Write("desktop/project/src/main.cs", new byte[1]);
        _offer.Offer(["desktop/project"], _fs);

        _offer.Allows("desktop/project").ShouldBeTrue();
        _offer.Allows("desktop/project/src/main.cs").ShouldBeTrue();
        _offer.Allows("desktop/projectile.txt").ShouldBeFalse("a prefix is not a parent");
    }

    [Theory]
    [InlineData("desktop/shared.txt/../../secrets/passwords.txt")]
    [InlineData("desktop/../secrets/passwords.txt")]
    [InlineData("desktop/shared.txt/..")]
    public void Traversal_dressed_up_as_an_allowed_path_is_refused(string path)
    {
        _fs.Write("desktop/shared.txt", new byte[1]);
        _offer.Offer(["desktop/shared.txt"], _fs);

        _offer.Allows(path).ShouldBeFalse();
    }

    [Fact]
    public void Withdrawing_closes_the_door_again()
    {
        _fs.Write("desktop/shared.txt", new byte[1]);
        _offer.Offer(["desktop/shared.txt"], _fs);
        _offer.Allows("desktop/shared.txt").ShouldBeTrue();

        _offer.Withdraw();

        _offer.Allows("desktop/shared.txt").ShouldBeFalse();
        _offer.Current.ShouldBeNull();
    }

    [Fact]
    public void Copying_something_else_replaces_what_the_peer_may_read()
    {
        _fs.Write("desktop/first.txt", new byte[1]);
        _fs.Write("desktop/second.txt", new byte[1]);
        _offer.Offer(["desktop/first.txt"], _fs);

        _offer.Offer(["desktop/second.txt"], _fs);

        _offer.Allows("desktop/second.txt").ShouldBeTrue();
        _offer.Allows("desktop/first.txt").ShouldBeFalse("it is no longer on the clipboard");
    }
}
