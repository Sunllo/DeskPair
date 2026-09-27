namespace DeskPair.Platform.Abstractions.Clipboard;

/// <summary>
/// One file offered by a copy, addressed by <c>Id</c> and never by position: a second copy while the first is
/// still being pasted would shift every index under the reader. <c>RelativePath</c> is relative to the
/// listing's root, '/'-separated, and has already been validated against traversal by the decoder.
/// </summary>
public sealed record FilePromiseEntry(long Id, string RelativePath, long Size, DateTimeOffset Modified, bool IsDirectory);

/// <summary>What a copy on the other machine offered. Carries no bytes.</summary>
public sealed record FilePromiseListing(string Token, string RemoteRoot, IReadOnlyList<FilePromiseEntry> Entries);

/// <summary>
/// Fetches one promised file, on demand, when something actually pastes. Implemented by the session that
/// received the listing; the clipboard calls it from whatever thread its platform delivers the paste on.
/// </summary>
public interface IFilePromiseSource
{
    /// <summary>
    /// The flat set of files actually being offered, with every directory in the listing expanded. Called
    /// when something pastes and not when something copies, so a directory nobody pastes is never walked;
    /// the result is cached, because a paste asks for it and then asks again per file.
    /// </summary>
    Task<IReadOnlyList<FilePromiseEntry>> ResolveAsync(CancellationToken ct);

    /// <summary>
    /// Where the file with <paramref name="id"/> will be written, whether or not it exists yet. Windows needs
    /// this before the fetch starts, because it hands out a stream that tails the file as it grows.
    /// </summary>
    string StagedPathFor(long id);

    /// <summary>
    /// Writes the file with <paramref name="id"/> to <paramref name="destinationPath"/>, creating parent
    /// directories. Returns when the file is complete; throws if the transfer fails or is cancelled.
    /// </summary>
    Task FetchAsync(long id, string destinationPath, IProgress<long>? written, CancellationToken ct);
}

/// <summary>
/// A clipboard that can offer files it does not yet have. Optional: a platform that cannot do this simply
/// does not implement it, or reports <see cref="CanPromiseFiles"/> false, and the file item is dropped.
/// </summary>
public interface IFilePromiseClipboard
{
    /// <summary>False when the platform is present but this session cannot promise — no display, no support.</summary>
    bool CanPromiseFiles { get; }

    /// <summary>
    /// The absolute local paths the user has copied, or empty. Only paths: reading them is the other end's
    /// business and happens when it pastes, which is the whole point.
    /// </summary>
    ValueTask<IReadOnlyList<string>> ReadCopiedFilePathsAsync(CancellationToken ct);

    /// <summary>
    /// Publishes <paramref name="items"/> and the file promise together. They travel in one call because
    /// Windows needs the promise and the other formats to be a single OLE transaction and macOS needs them in
    /// one <c>writeObjects:</c>; writing the text separately afterwards corrupts the promise on both.
    /// </summary>
    ValueTask WriteWithPromiseAsync(
        IReadOnlyList<ClipboardItem> items,
        FilePromiseListing listing,
        IFilePromiseSource source,
        CancellationToken ct);
}
