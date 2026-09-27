using System.Text;
using Shouldly;
using DeskPair.Platform.Linux.Clipboard;

namespace DeskPair.Platform.Linux.Tests;

/// <summary>
/// The two file-list conventions, whose differences are invisible and load-bearing. No display needed.
/// </summary>
public sealed class UriListCodecTests
{
    [Fact]
    public void A_uri_list_is_crlf_separated_and_ends_with_one()
    {
        string text = Encoding.UTF8.GetString(UriListCodec.UriList(["/home/alice/a.txt", "/home/alice/b.txt"]));

        text.ShouldBe("file:///home/alice/a.txt\r\nfile:///home/alice/b.txt\r\n");
    }

    [Fact]
    public void Gnome_copied_files_names_the_operation_and_has_no_trailing_newline()
    {
        string text = Encoding.UTF8.GetString(UriListCodec.GnomeCopiedFiles(["/home/alice/a.txt"]));

        // A trailing LF reads as one more, empty, URI in some Nautilus versions, and the paste does nothing.
        text.ShouldBe("copy\nfile:///home/alice/a.txt");
        text.ShouldNotEndWith("\n");
    }

    [Theory]
    [InlineData("/home/alice/my file.txt", "file:///home/alice/my%20file.txt")]
    [InlineData("/home/alice/a#b.txt", "file:///home/alice/a%23b.txt")]
    [InlineData("/home/alice/100%.txt", "file:///home/alice/100%25.txt")]
    [InlineData("/home/alice/檔案.txt", "file:///home/alice/%E6%AA%94%E6%A1%88.txt")]
    public void Characters_that_would_truncate_the_uri_are_escaped(string path, string expected)
    {
        // Unescaped, the receiver copies a file that does not exist and says nothing went wrong.
        UriListCodec.FileUri(path).ShouldBe(expected);
    }

    [Fact]
    public void Either_convention_reads_back_to_the_same_paths()
    {
        string[] paths = ["/home/alice/my file.txt", "/home/alice/b.txt"];

        UriListCodec.ParsePaths(UriListCodec.UriList(paths)).ShouldBe(paths);
        UriListCodec.ParsePaths(UriListCodec.GnomeCopiedFiles(paths)).ShouldBe(paths);
        UriListCodec.ParsePaths(UriListCodec.GnomeCopiedFiles(paths, cut: true)).ShouldBe(paths);
    }

    [Fact]
    public void Comments_blanks_and_things_that_are_not_files_are_ignored()
    {
        byte[] mixed = Encoding.UTF8.GetBytes("# a comment\r\n\r\nhttps://example.com/x\r\nfile:///home/alice/a.txt\r\n");

        UriListCodec.ParsePaths(mixed).ShouldBe(["/home/alice/a.txt"]);
    }

    [Fact]
    public void Nothing_copied_reads_back_as_nothing()
    {
        UriListCodec.ParsePaths([]).ShouldBeEmpty();
        UriListCodec.ParsePaths(UriListCodec.GnomeCopiedFiles([])).ShouldBeEmpty();
    }
}
