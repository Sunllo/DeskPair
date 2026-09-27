using Microsoft.Extensions.Logging;
using DeskPair.Core.FileTransfer;
using DeskPair.Platform.Abstractions.Clipboard;

namespace DeskPair.Core.Clipboard;

/// <summary>
/// What this machine has offered to the other one, and the only thing the peer is then allowed to read.
///
/// The peer-facing half matters as much as the listing. A transfer engine answers whatever path the peer asks
/// for unless something says otherwise, and until now the controller's engine said nothing — a host could ask
/// it for any file on the machine. Copying files makes that path reachable in normal use, so the engine is
/// given a guard that allows exactly what was copied and nothing else.
/// </summary>
public sealed class LocalFileOffer
{
    private readonly ILogger _log;
    private readonly Lock _gate = new();
    private string[] _allowed = [];

    public LocalFileOffer(ILogger log)
    {
        _log = log;
    }

    /// <summary>The listing currently offered, or null.</summary>
    public FilePromiseListing? Current { get; private set; }

    /// <summary>
    /// Turns what the local user copied into a listing to publish, and opens the guard for exactly those
    /// paths. Only the names and sizes are read here; nothing is opened.
    /// </summary>
    public ClipboardItem? Offer(IReadOnlyList<string> absolutePaths, IFileSystem fs)
    {
        if (absolutePaths.Count == 0)
        {
            Withdraw();
            return null;
        }

        string root = fs.GetDirectoryName(absolutePaths[0]);
        var entries = new List<FilePromiseEntry>(absolutePaths.Count);
        var allowed = new List<string>(absolutePaths.Count + 1);
        long id = 1;

        foreach (string path in absolutePaths)
        {
            // One Ctrl+C selects within one folder on every platform we support, so anything else is either a
            // synthetic clipboard or a bug; dropping it keeps root meaningful.
            if (!string.Equals(fs.GetDirectoryName(path), root, StringComparison.Ordinal))
            {
                _log.LogDebug("Not offering {Path}: it is not under the copied selection's folder", path);
                continue;
            }

            FsStat? stat = fs.Stat(path);
            if (stat is null)
            {
                continue;
            }

            string name = fs.GetFileName(path);
            if (!FilePromiseCodec.IsAcceptable(name))
            {
                _log.LogDebug("Not offering a copied file whose name the other side could not store safely");
                continue;
            }

            entries.Add(new FilePromiseEntry(id++, name, stat.IsDirectory ? 0 : stat.Size, stat.Modified, stat.IsDirectory));
            allowed.Add(path);
        }

        if (entries.Count == 0)
        {
            Withdraw();
            return null;
        }

        var listing = new FilePromiseListing(Guid.NewGuid().ToString("N")[..12], root, entries);
        lock (_gate)
        {
            Current = listing;
            _allowed = [.. allowed];
        }

        _log.LogDebug("Offering {Count} copied item(s) to the peer, token {Token}", entries.Count, listing.Token);
        return FilePromiseCodec.Encode(listing);
    }

    /// <summary>Stops offering anything. The peer may then read nothing at all.</summary>
    public void Withdraw()
    {
        lock (_gate)
        {
            Current = null;
            _allowed = [];
        }
    }

    /// <summary>
    /// Whether the peer may read <paramref name="path"/>: it must be one of the copied items or live under
    /// one of them. Everything else on this machine stays unreachable.
    /// </summary>
    public bool Allows(string path)
    {
        string[] allowed;
        lock (_gate)
        {
            allowed = _allowed;
        }

        if (allowed.Length == 0 || string.IsNullOrEmpty(path))
        {
            return false;
        }

        string normalised = Normalise(path);
        foreach (string candidate in allowed)
        {
            string offered = Normalise(candidate);
            if (normalised.Equals(offered, PathComparison))
            {
                return true;
            }

            // A copied directory is offered as one entry and read as its contents.
            if (normalised.StartsWith(offered + '/', PathComparison))
            {
                return true;
            }
        }

        return false;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Both separators mean the same thing to the peer, and a trailing one means nothing, so neither may
    /// decide whether a path is allowed. Traversal is refused outright rather than resolved, because
    /// resolving it here would mean touching the file system on the strength of what the peer sent.
    /// </summary>
    private static string Normalise(string path)
    {
        string result = path.Replace('\\', '/').TrimEnd('/');
        return result.Contains("/../", StringComparison.Ordinal) || result.EndsWith("/..", StringComparison.Ordinal)
            ? "\0invalid"
            : result;
    }
}
