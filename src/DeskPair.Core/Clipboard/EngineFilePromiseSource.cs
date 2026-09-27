using Microsoft.Extensions.Logging;
using DeskPair.Core.FileTransfer;
using DeskPair.Platform.Abstractions.Clipboard;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.Clipboard;

/// <summary>
/// Fetches promised files over an existing <see cref="FileTransferEngine"/>. Both ends use this: the
/// controller drives its session's engine, the host drives the one for the connection that sent the listing.
///
/// One transfer job per file, not one for the listing. A job sends its files strictly in order, so a consumer
/// that asks for the third file before the first — which Explorer and Finder both do — would deadlock waiting
/// on a job that is waiting on it.
/// </summary>
public sealed class EngineFilePromiseSource : IFilePromiseSource
{
    private readonly FileTransferEngine _engine;
    private readonly FilePromiseListing _listing;
    private readonly ClipboardStaging _staging;
    private readonly ILogger _log;
    private readonly Dictionary<long, FilePromiseEntry> _byId;

    // A directory is one entry until someone pastes it; then it becomes its files, once.
    private readonly SemaphoreSlim _expansion = new(1, 1);
    private Task<IReadOnlyList<FilePromiseEntry>>? _resolved;

    public EngineFilePromiseSource(
        FileTransferEngine engine,
        FilePromiseListing listing,
        ClipboardStaging staging,
        ILogger log)
    {
        _engine = engine;
        _listing = listing;
        _staging = staging;
        _log = log;
        _byId = listing.Entries.ToDictionary(e => e.Id);
    }

    public FilePromiseListing Listing => _listing;

    /// <summary>
    /// The top-level items as the peer copied them, directories included. Windows flattens these with
    /// <see cref="ResolveAsync"/> because its descriptor is a flat list of files; macOS and Linux hand a
    /// folder over whole, which is the only way its contents keep their shape.
    /// </summary>
    public IReadOnlyList<FilePromiseEntry> TopLevel => _listing.Entries;

    public string StagedPathFor(long id) =>
        _byId.TryGetValue(id, out FilePromiseEntry? entry)
            ? _staging.PathFor(_listing.Token, entry.RelativePath)
            : throw new InvalidOperationException($"No promised file with id {id}.");

    /// <summary>Drops everything fetched for this promise. Called when the promise stops being the clipboard.</summary>
    public void Discard() => _staging.Discard(_listing.Token);

    public async Task FetchAsync(long id, string destinationPath, IProgress<long>? written, CancellationToken ct)
    {
        if (!_byId.TryGetValue(id, out FilePromiseEntry? entry))
        {
            throw new InvalidOperationException($"No promised file with id {id}.");
        }

        string remotePath = Combine(_listing.RemoteRoot, entry.RelativePath);

        // The engine downloads a directory's contents into the directory it is given, without repeating the
        // directory's own name, so a promised folder is fetched into the folder and a promised file beside it.
        // macOS hands a whole folder over as one URL and needs this; Windows only ever asks for files.
        string destinationDir = entry.IsDirectory
            ? destinationPath
            : Path.GetDirectoryName(destinationPath)
              ?? throw new InvalidOperationException($"Destination has no directory: {destinationPath}");

        _log.LogDebug("Fetching promised file {Path} for token {Token}", entry.RelativePath, _listing.Token);

        int job = await _engine.StartDownloadAsync(remotePath, destinationDir, includeHidden: true, ct).ConfigureAwait(false);

        void OnProgress(TransferJobSnapshot s)
        {
            if (s.Id == job)
            {
                written?.Report(s.TransferredBytes);
            }
        }

        _engine.Progress += OnProgress;
        try
        {
            await _engine.WaitForJobAsync(job, ct).ConfigureAwait(false);
        }
        finally
        {
            _engine.Progress -= OnProgress;
        }
    }

    public async Task<IReadOnlyList<FilePromiseEntry>> ResolveAsync(CancellationToken ct)
    {
        await _expansion.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Cached as a Task, not as a list: a paste asks what is on offer and then asks again per file,
            // and on Windows those calls arrive on different threads.
            _resolved ??= ExpandAsync(ct);
            return await _resolved.ConfigureAwait(false);
        }
        finally
        {
            _expansion.Release();
        }
    }

    /// <summary>Walks every promised directory, once, and returns the flat list of files they contain.</summary>
    private async Task<IReadOnlyList<FilePromiseEntry>> ExpandAsync(CancellationToken ct)
    {
        var files = new List<FilePromiseEntry>();
        long next = 1L << 32;   // above every id the peer allocated, so an expanded file cannot shadow one

        foreach (FilePromiseEntry entry in _listing.Entries)
        {
            if (!entry.IsDirectory)
            {
                files.Add(entry);
                continue;
            }

            FileDirectory contents = await _engine
                .ListAllFilesAsync(Combine(_listing.RemoteRoot, entry.RelativePath), includeHidden: true, ct)
                .ConfigureAwait(false);

            foreach (FileEntry file in contents.Entries)
            {
                if (file.Type == FileType.FtDir)
                {
                    continue;
                }

                string relative = entry.RelativePath + "/" + file.Name.Replace('\\', '/');
                if (!FilePromiseCodec.IsAcceptable(relative))
                {
                    _log.LogWarning("Dropping a promised file under {Directory} whose name is not usable", entry.RelativePath);
                    continue;
                }

                var child = new FilePromiseEntry(
                    next++,
                    relative,
                    (long)file.Size,
                    DateTimeOffset.FromUnixTimeSeconds((long)file.ModifiedUnix),
                    IsDirectory: false);
                _byId[child.Id] = child;
                files.Add(child);
            }
        }

        _log.LogDebug("Promise {Token} resolved to {Count} file(s)", _listing.Token, files.Count);
        return files;
    }

    /// <summary>The peer's paths use its own separator; the root is echoed back exactly as it was sent.</summary>
    private static string Combine(string root, string relative) =>
        root.Length == 0 ? relative
        : root.EndsWith('/') || root.EndsWith('\\') ? root + relative
        : $"{root}/{relative}";
}
