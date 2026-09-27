using System.Text;

namespace DeskPair.Platform.Linux.Clipboard;

/// <summary>
/// The two conventions X11 file managers actually read, which differ from each other in ways that look like
/// nothing and are not.
///
/// <c>text/uri-list</c> (RFC 2483) is CRLF-separated and ends with a trailing CRLF.
/// <c>x-special/gnome-copied-files</c> is "copy" or "cut", then one LF-separated URI per line, and must
/// <em>not</em> end with a newline — some Nautilus versions read a trailing LF as one more, empty, URI and
/// paste nothing.
/// </summary>
internal static class UriListCodec
{
    /// <summary>RFC 2483: CRLF between URIs and one at the end.</summary>
    public static byte[] UriList(IEnumerable<string> paths) =>
        Encoding.UTF8.GetBytes(string.Concat(paths.Select(p => FileUri(p) + "\r\n")));

    /// <summary>GNOME's own: an operation, then LF-separated URIs, with no trailing newline.</summary>
    public static byte[] GnomeCopiedFiles(IEnumerable<string> paths, bool cut = false) =>
        Encoding.UTF8.GetBytes(string.Join('\n', new[] { cut ? "cut" : "copy" }.Concat(paths.Select(FileUri))));

    /// <summary>KDE marks a cut with a single "1"; "0" means copy. Absent means copy too.</summary>
    public static byte[] KdeCutSelection(bool cut) => Encoding.UTF8.GetBytes(cut ? "1" : "0");

    /// <summary>Reads either convention back into absolute paths, ignoring the operation line and blanks.</summary>
    public static IReadOnlyList<string> ParsePaths(ReadOnlySpan<byte> bytes)
    {
        string text = Encoding.UTF8.GetString(bytes);
        var paths = new List<string>();
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line is "copy" or "cut" || line.StartsWith('#'))
            {
                continue;
            }

            if (!line.StartsWith("file://", StringComparison.Ordinal))
            {
                continue;   // a URI we cannot fetch from is not a file we can offer
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out Uri? uri) && uri.IsFile)
            {
                paths.Add(uri.LocalPath);
            }
        }

        return paths;
    }

    /// <summary>
    /// A file URI with every reserved character escaped. Left alone, a name with a space or a '#' in it
    /// truncates the URI at that point and the receiver copies a file that does not exist.
    /// </summary>
    public static string FileUri(string absolutePath)
    {
        var builder = new StringBuilder("file://");
        foreach (string segment in absolutePath.Split('/'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            builder.Append('/').Append(Uri.EscapeDataString(segment));
        }

        return builder.ToString();
    }
}
