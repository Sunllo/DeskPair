using System.Buffers;
using System.Collections.Concurrent;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using DeskPair.Core.Session;
using DeskPair.Protocol;
using DeskPair.Protocol.Messages;

namespace DeskPair.Core.FileTransfer;

/// <summary>Decides what to do when an incoming file already exists locally.</summary>
public enum OverwriteDecision
{
    Overwrite,
    Skip,
    Resume,
}

public interface IFileTransferPolicy
{
    /// <param name="localPath">Where the file would be written.</param>
    /// <param name="existing">Local file state, or null when absent.</param>
    /// <param name="incoming">The file the peer offers.</param>
    /// <param name="partialBytes">Bytes already in a partial download, or 0.</param>
    OverwriteDecision Decide(string localPath, FsStat? existing, TransferFile incoming, long partialBytes);
}

/// <summary>Skip identical files, resume partial ones, overwrite everything else.</summary>
public sealed class DefaultFileTransferPolicy : IFileTransferPolicy
{
    public static DefaultFileTransferPolicy Instance { get; } = new();

    public OverwriteDecision Decide(string localPath, FsStat? existing, TransferFile incoming, long partialBytes)
    {
        if (existing is { IsDirectory: false } && existing.Size == incoming.Size && Math.Abs((existing.Modified - incoming.Modified).TotalSeconds) < 2)
        {
            return OverwriteDecision.Skip;
        }

        return partialBytes > 0 && partialBytes < incoming.Size ? OverwriteDecision.Resume : OverwriteDecision.Overwrite;
    }
}

/// <summary>
/// One engine per session, on both sides. It answers the peer's file actions against <see cref="IFileSystem"/>
/// and drives the jobs this side initiated. Wire flow for a download (this side receives):
/// Send → [Dir listing] → per file: Digest → SendConfirm(skip|offset) → Block* → Done → … → Done(file_num = -1).
/// An upload is the mirror image with the receiver confirming from the entries in the Receive request.
/// </summary>
public sealed class FileTransferEngine : IAsyncDisposable
{
    public const string PartialSuffix = ".sunllo-part";
    /// <summary>Where the host's job ids start, so the two ends' counters can never produce the same number.</summary>
    private const int HostIdBase = 0x4000_0000;

    private const int AllFilesDone = -1;      // sender -> receiver: no more files
    private const int AllFilesReceived = -2;  // receiver -> sender: everything is on disk
    private static readonly TimeSpan AckTimeout = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fs;
    private readonly Func<Message, MessagePriority, CancellationToken, ValueTask> _send;
    private readonly IFileTransferPolicy _policy;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<int, TransferJob> _sendJobs = new();
    private readonly ConcurrentDictionary<int, TransferJob> _receiveJobs = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<FileDirectory>> _pendingListings = new();

    // A finished job is removed from its dictionary, so without this a caller that asks how a job ended after
    // it ended is told "fine" whatever happened. Bounded, because a long session finishes a lot of jobs.
    private readonly ConcurrentDictionary<int, Task> _outcomes = new();
    private readonly ConcurrentQueue<int> _outcomeOrder = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _idBase;
    private int _nextId;

    public FileTransferEngine(IFileSystem fs, Func<Message, MessagePriority, CancellationToken, ValueTask> send, ILogger log, IFileTransferPolicy? policy = null, TimeProvider? time = null, FileTransferSide side = FileTransferSide.Controller)
    {
        _fs = fs;
        _send = send;
        _log = log;
        _policy = policy ?? DefaultFileTransferPolicy.Instance;
        _time = time ?? TimeProvider.System;
        _idBase = side == FileTransferSide.Host ? HostIdBase : 0;
    }

    /// <summary>Progress and state changes for every job on this side, at most every 250 ms per job plus every state change.</summary>
    public event Action<TransferJobSnapshot>? Progress;

    /// <summary>The peer refused or failed a job: (job id, the path it concerns, the error text).</summary>
    public event Action<int, string, string>? RemoteError;

    /// <summary>Restricts what the peer may access; null allows everything.</summary>
    public Func<string, bool>? IsPathAllowed { get; set; }

    public IEnumerable<TransferJobSnapshot> Jobs => _sendJobs.Values.Concat(_receiveJobs.Values).Select(j => j.Snapshot(_time));

    /// <summary>
    /// A fresh id for a job this side starts. Both ends number their own jobs and a job the peer started is
    /// stored under the peer's id, so the two counters must not overlap: with clipboard paste either end can
    /// start a transfer, and two jobs sharing an id would deliver one job's blocks into the other's file.
    /// </summary>
    public int NextJobId() => _idBase + Interlocked.Increment(ref _nextId);

    // ---- requests this side initiates ----

    public async Task<FileDirectory> ListDirectoryAsync(string remotePath, bool includeHidden, CancellationToken ct)
    {
        int id = NextJobId();
        var tcs = new TaskCompletionSource<FileDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingListings[id] = tcs;
        try
        {
            await _send(new Message { FileAction = new FileAction { ReadDir = new ReadDir { Id = id, Path = remotePath, IncludeHidden = includeHidden } } }, MessagePriority.Control, ct).ConfigureAwait(false);
            using CancellationTokenRegistration reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingListings.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// Like <see cref="ListDirectoryAsync"/> but recursive: every file under <paramref name="remotePath"/>,
    /// with paths relative to it. This is how a copied directory is expanded at paste time rather than at
    /// copy time, so that Ctrl+C on a large tree costs nothing.
    /// </summary>
    public async Task<FileDirectory> ListAllFilesAsync(string remotePath, bool includeHidden, CancellationToken ct)
    {
        int id = NextJobId();
        var tcs = new TaskCompletionSource<FileDirectory>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingListings[id] = tcs;
        try
        {
            await _send(new Message { FileAction = new FileAction { AllFiles = new ReadAllFiles { Id = id, Path = remotePath, IncludeHidden = includeHidden } } }, MessagePriority.Control, ct).ConfigureAwait(false);
            using CancellationTokenRegistration reg = ct.Register(() => tcs.TrySetCanceled(ct));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingListings.TryRemove(id, out _);
        }
    }

    /// <summary>Asks the peer to send <paramref name="remotePath"/> (file or directory) into <paramref name="localDir"/>.</summary>
    public async Task<int> StartDownloadAsync(string remotePath, string localDir, bool includeHidden, CancellationToken ct)
    {
        int id = NextJobId();
        var job = new TransferJob { Id = id, IsSending = false, RemotePath = remotePath, LocalPath = localDir, IncludeHidden = includeHidden };
        _receiveJobs[id] = job;
        await _send(new Message { FileAction = new FileAction { Send = new FileTransferSendRequest { Id = id, Path = remotePath, IncludeHidden = includeHidden } } }, MessagePriority.Control, ct).ConfigureAwait(false);
        Report(job);
        return id;
    }

    /// <summary>Sends <paramref name="localPath"/> (file or directory) into the peer's <paramref name="remoteDir"/>.</summary>
    public async Task<int> StartUploadAsync(string localPath, string remoteDir, bool includeHidden, CancellationToken ct)
    {
        int id = NextJobId();
        var job = new TransferJob { Id = id, IsSending = true, RemotePath = remoteDir, LocalPath = localPath, IncludeHidden = includeHidden };
        foreach ((string rel, long size, DateTimeOffset modified) in _fs.EnumerateFiles(localPath, includeHidden))
        {
            job.Files.Add(new TransferFile(rel, size, modified));
            job.TotalBytes += size;
        }

        _sendJobs[id] = job;
        var request = new FileTransferReceiveRequest { Id = id, Path = remoteDir, TotalSize = (ulong)job.TotalBytes };
        request.Files.AddRange(job.Files.Select(ToEntry));
        await _send(new Message { FileAction = new FileAction { Receive = request } }, MessagePriority.Control, ct).ConfigureAwait(false);
        StartSending(job);
        return id;
    }

    /// <summary>
    /// Waits for a job to reach a terminal state. Throws <see cref="FileTransferFailedException"/> if it
    /// failed and <see cref="OperationCanceledException"/> if it was cancelled — a caller that pastes has to
    /// be able to tell those apart from success before it tells the user the file arrived.
    /// </summary>
    public Task WaitForJobAsync(int id, CancellationToken ct)
    {
        TransferJob? job = _sendJobs.GetValueOrDefault(id) ?? _receiveJobs.GetValueOrDefault(id);
        if (job is not null)
        {
            return job.Completion.Task.WaitAsync(ct);
        }

        // Already over. Report how it ended, not merely that it did.
        return _outcomes.TryGetValue(id, out Task? outcome) ? outcome.WaitAsync(ct) : Task.CompletedTask;
    }

    public async Task CancelAsync(int id, CancellationToken ct = default)
    {
        TransferJob? job = _sendJobs.GetValueOrDefault(id) ?? _receiveJobs.GetValueOrDefault(id);
        if (job is null)
        {
            return;
        }

        await _send(new Message { FileAction = new FileAction { Cancel = new FileTransferCancel { Id = id } } }, MessagePriority.Control, ct).ConfigureAwait(false);
        Finish(job, TransferState.Cancelled, "cancelled");
    }

    public ValueTask CreateDirectoryAsync(string remotePath, CancellationToken ct = default) =>
        _send(new Message { FileAction = new FileAction { Create = new FileDirCreate { Id = NextJobId(), Path = remotePath } } }, MessagePriority.Control, ct);

    public ValueTask RemoveFileAsync(string remotePath, CancellationToken ct = default) =>
        _send(new Message { FileAction = new FileAction { RemoveFile = new FileRemoveFile { Id = NextJobId(), Path = remotePath } } }, MessagePriority.Control, ct);

    public ValueTask RemoveDirectoryAsync(string remotePath, bool recursive, CancellationToken ct = default) =>
        _send(new Message { FileAction = new FileAction { RemoveDir = new FileRemoveDir { Id = NextJobId(), Path = remotePath, Recursive = recursive } } }, MessagePriority.Control, ct);

    public ValueTask RenameAsync(string remotePath, string newName, CancellationToken ct = default) =>
        _send(new Message { FileAction = new FileAction { Rename = new FileRename { Id = NextJobId(), Path = remotePath, NewName = newName } } }, MessagePriority.Control, ct);

    // ---- wire input ----

    public async ValueTask HandleAsync(Message message, CancellationToken ct)
    {
        try
        {
            switch (message.UnionCase)
            {
                case Message.UnionOneofCase.FileAction:
                    await HandleActionAsync(message.FileAction, ct).ConfigureAwait(false);
                    break;
                case Message.UnionOneofCase.FileResponse:
                    await HandleResponseAsync(message.FileResponse, ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "File transfer message {Case} failed", message.UnionCase);
        }
    }

    private async ValueTask HandleActionAsync(FileAction action, CancellationToken ct)
    {
        switch (action.UnionCase)
        {
            case FileAction.UnionOneofCase.ReadDir:
                await AnswerListingAsync(action.ReadDir.Id, action.ReadDir.Path, action.ReadDir.IncludeHidden, recursive: false, ct).ConfigureAwait(false);
                break;
            case FileAction.UnionOneofCase.AllFiles:
                await AnswerListingAsync(action.AllFiles.Id, action.AllFiles.Path, action.AllFiles.IncludeHidden, recursive: true, ct).ConfigureAwait(false);
                break;
            case FileAction.UnionOneofCase.Send:
                await BeginSendingToPeerAsync(action.Send, ct).ConfigureAwait(false);
                break;
            case FileAction.UnionOneofCase.Receive:
                BeginReceivingFromPeer(action.Receive);
                break;
            case FileAction.UnionOneofCase.SendConfirm:
                if (_sendJobs.TryGetValue(action.SendConfirm.Id, out TransferJob? job))
                {
                    job.Confirmation(action.SendConfirm.FileNum).TrySetResult((action.SendConfirm.UnionCase == FileTransferSendConfirm.UnionOneofCase.Skip && action.SendConfirm.Skip, action.SendConfirm.OffsetBlk));
                }

                break;
            case FileAction.UnionOneofCase.Cancel:
                if (_sendJobs.TryGetValue(action.Cancel.Id, out TransferJob? s))
                {
                    Finish(s, TransferState.Cancelled, "cancelled by peer");
                }

                if (_receiveJobs.TryGetValue(action.Cancel.Id, out TransferJob? r))
                {
                    Finish(r, TransferState.Cancelled, "cancelled by peer");
                }

                break;
            case FileAction.UnionOneofCase.Create:
                await GuardedAsync(action.Create.Id, action.Create.Path, () => _fs.CreateDirectory(action.Create.Path), ct).ConfigureAwait(false);
                break;
            case FileAction.UnionOneofCase.RemoveFile:
                await GuardedAsync(action.RemoveFile.Id, action.RemoveFile.Path, () => _fs.DeleteFile(action.RemoveFile.Path), ct).ConfigureAwait(false);
                break;
            case FileAction.UnionOneofCase.RemoveDir:
                await GuardedAsync(action.RemoveDir.Id, action.RemoveDir.Path, () => _fs.DeleteDirectory(action.RemoveDir.Path, action.RemoveDir.Recursive), ct).ConfigureAwait(false);
                break;
            case FileAction.UnionOneofCase.Rename:
                await GuardedAsync(action.Rename.Id, action.Rename.Path, () =>
                {
                    PathGuard.ValidateRelative(action.Rename.NewName);
                    _fs.Move(action.Rename.Path, _fs.Combine(_fs.GetDirectoryName(action.Rename.Path), action.Rename.NewName));
                }, ct).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask HandleResponseAsync(FileResponse response, CancellationToken ct)
    {
        switch (response.UnionCase)
        {
            case FileResponse.UnionOneofCase.Dir:
                if (_pendingListings.TryRemove(response.Dir.Id, out TaskCompletionSource<FileDirectory>? tcs))
                {
                    tcs.TrySetResult(response.Dir);
                }
                else if (_receiveJobs.TryGetValue(response.Dir.Id, out TransferJob? job) && job.Files.Count == 0)
                {
                    foreach (FileEntry e in response.Dir.Entries.Where(e => e.Type == FileType.FtFile))
                    {
                        job.Files.Add(new TransferFile(e.Name, (long)e.Size, DateTimeOffset.FromUnixTimeSeconds((long)e.ModifiedUnix)));
                        job.TotalBytes += (long)e.Size;
                    }

                    job.State = TransferState.Running;
                    job.StartedTimestamp = _time.GetTimestamp();
                    Report(job);
                }

                break;
            case FileResponse.UnionOneofCase.Digest:
                await ConfirmDownloadFileAsync(response.Digest, ct).ConfigureAwait(false);
                break;
            case FileResponse.UnionOneofCase.Block:
                await WriteBlockAsync(response.Block, ct).ConfigureAwait(false);
                break;
            case FileResponse.UnionOneofCase.Done when response.Done.FileNum == AllFilesReceived:
                if (_sendJobs.TryGetValue(response.Done.Id, out TransferJob? acked))
                {
                    acked.Acknowledged.TrySetResult();
                }

                break;
            case FileResponse.UnionOneofCase.Done:
                await FinishFileAsync(response.Done.Id, response.Done.FileNum).ConfigureAwait(false);
                break;
            case FileResponse.UnionOneofCase.Error:
                RemoteError?.Invoke(
                    response.Error.Id,
                    (_receiveJobs.GetValueOrDefault(response.Error.Id) ?? _sendJobs.GetValueOrDefault(response.Error.Id))?.RemotePath ?? string.Empty,
                    response.Error.Error);
                if (_receiveJobs.TryGetValue(response.Error.Id, out TransferJob? rj))
                {
                    Finish(rj, TransferState.Failed, response.Error.Error);
                }

                if (_sendJobs.TryGetValue(response.Error.Id, out TransferJob? sj))
                {
                    Finish(sj, TransferState.Failed, response.Error.Error);
                }

                // A directory listing that the host refuses (e.g. outside an allow-list root) comes back as an
                // error carrying the listing's id; fault the waiter so ListDirectoryAsync throws instead of
                // hanging forever on a Dir response that will never arrive.
                if (_pendingListings.TryRemove(response.Error.Id, out TaskCompletionSource<FileDirectory>? pl))
                {
                    pl.TrySetException(new IOException(response.Error.Error));
                }

                break;
        }
    }

    // ---- answering the peer ----

    private async ValueTask AnswerListingAsync(int id, string path, bool includeHidden, bool recursive, CancellationToken ct)
    {
        try
        {
            CheckAllowed(path);
            var dir = new FileDirectory { Id = id, Path = path };
            if (recursive)
            {
                dir.Entries.AddRange(_fs.EnumerateFiles(path, includeHidden).Select(f => new FileEntry { Type = FileType.FtFile, Name = f.RelativePath, Size = (ulong)f.Size, ModifiedUnix = (ulong)f.Modified.ToUnixTimeSeconds() }));
            }
            else
            {
                dir.Entries.AddRange(_fs.ListDirectory(path, includeHidden).Select(e => new FileEntry
                {
                    Type = e.Kind switch { FsEntryKind.File => FileType.FtFile, FsEntryKind.Symlink => FileType.FtSymlink, _ => FileType.FtDir },
                    Name = e.Name,
                    Size = (ulong)e.Size,
                    ModifiedUnix = (ulong)Math.Max(0, e.Modified.ToUnixTimeSeconds()),
                }));
            }

            await _send(new Message { FileResponse = new FileResponse { Dir = dir } }, MessagePriority.Control, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PathGuardException)
        {
            await SendErrorAsync(id, 0, e.Message, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask GuardedAsync(int id, string path, Action action, CancellationToken ct)
    {
        try
        {
            CheckAllowed(path);
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PathGuardException)
        {
            await SendErrorAsync(id, 0, e.Message, ct).ConfigureAwait(false);
        }
    }

    private async ValueTask BeginSendingToPeerAsync(FileTransferSendRequest request, CancellationToken ct)
    {
        var job = new TransferJob { Id = request.Id, IsSending = true, RemotePath = string.Empty, LocalPath = request.Path, IncludeHidden = request.IncludeHidden };
        try
        {
            CheckAllowed(request.Path);
            foreach ((string rel, long size, DateTimeOffset modified) in _fs.EnumerateFiles(request.Path, request.IncludeHidden))
            {
                job.Files.Add(new TransferFile(rel, size, modified));
                job.TotalBytes += size;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PathGuardException)
        {
            await SendErrorAsync(request.Id, 0, e.Message, ct).ConfigureAwait(false);
            return;
        }

        _sendJobs[request.Id] = job;
        var dir = new FileDirectory { Id = request.Id, Path = request.Path };
        dir.Entries.AddRange(job.Files.Select(ToEntry));
        await _send(new Message { FileResponse = new FileResponse { Dir = dir } }, MessagePriority.Bulk, ct).ConfigureAwait(false);
        StartSending(job, announceDigest: true);
    }

    private void BeginReceivingFromPeer(FileTransferReceiveRequest request)
    {
        var job = new TransferJob { Id = request.Id, IsSending = false, RemotePath = string.Empty, LocalPath = request.Path, IncludeHidden = false };
        foreach (FileEntry e in request.Files)
        {
            job.Files.Add(new TransferFile(e.Name, (long)e.Size, DateTimeOffset.FromUnixTimeSeconds((long)e.ModifiedUnix)));
            job.TotalBytes += (long)e.Size;
        }

        job.State = TransferState.Running;
        job.StartedTimestamp = _time.GetTimestamp();
        _receiveJobs[request.Id] = job;
        Report(job);
        _ = Task.Run(async () =>
        {
            try
            {
                CheckAllowed(request.Path);
                await ConfirmUploadFileAsync(job, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PathGuardException)
            {
                await SendErrorAsync(job.Id, job.FileIndex, e.Message, CancellationToken.None).ConfigureAwait(false);
                Finish(job, TransferState.Failed, e.Message);
            }
        });
    }

    // ---- sending side ----

    private void StartSending(TransferJob job, bool announceDigest = false)
    {
        job.State = TransferState.Running;
        job.StartedTimestamp = _time.GetTimestamp();
        Report(job);
        _ = Task.Run(() => SendLoopAsync(job, announceDigest));
    }

    private async Task SendLoopAsync(TransferJob job, bool announceDigest)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, job.Cancellation.Token);
        CancellationToken ct = linked.Token;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ProtocolConstants.FileBlockBytes);
        try
        {
            for (job.FileIndex = 0; job.FileIndex < job.Files.Count; job.FileIndex++)
            {
                TransferFile file = job.Files[job.FileIndex];
                if (announceDigest)
                {
                    await _send(new Message
                    {
                        FileResponse = new FileResponse
                        {
                            Digest = new FileTransferDigest
                            {
                                Id = job.Id,
                                FileNum = job.FileIndex,
                                FileSize = (ulong)file.Size,
                                LastModified = (ulong)file.Modified.ToUnixTimeSeconds(),
                                IsUpload = false,
                            },
                        },
                    }, MessagePriority.Bulk, ct).ConfigureAwait(false);
                }

                (bool skip, uint offsetBlock) = await job.Confirmation(job.FileIndex).Task.WaitAsync(ct).ConfigureAwait(false);
                if (skip)
                {
                    job.TransferredBytes += file.Size;
                    Report(job);
                    continue;
                }

                string path = job.Files.Count == 1 && _fs.Stat(job.LocalPath) is { IsDirectory: false } ? job.LocalPath : PathGuard.Join(_fs, job.LocalPath, file.RelativePath);
                await using Stream stream = _fs.OpenRead(path);
                long offset = (long)offsetBlock * ProtocolConstants.FileBlockBytes;
                if (offset > 0)
                {
                    stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
                    job.TransferredBytes += Math.Min(offset, stream.Length);
                }

                uint blk = offsetBlock;
                while (true)
                {
                    int n = await stream.ReadAtLeastAsync(buffer.AsMemory(0, ProtocolConstants.FileBlockBytes), ProtocolConstants.FileBlockBytes, throwOnEndOfStream: false, ct).ConfigureAwait(false);
                    if (n == 0)
                    {
                        break;
                    }

                    var block = new FileTransferBlock { Id = job.Id, FileNum = job.FileIndex, BlkId = blk++, Data = ByteString.CopyFrom(buffer.AsSpan(0, n)) };
                    await _send(new Message { FileResponse = new FileResponse { Block = block } }, MessagePriority.Bulk, ct).ConfigureAwait(false);
                    job.TransferredBytes += n;
                    ReportThrottled(job);
                    if (n < ProtocolConstants.FileBlockBytes)
                    {
                        break;
                    }
                }

                await _send(new Message { FileResponse = new FileResponse { Done = new FileTransferDone { Id = job.Id, FileNum = job.FileIndex } } }, MessagePriority.Bulk, ct).ConfigureAwait(false);
            }

            await _send(new Message { FileResponse = new FileResponse { Done = new FileTransferDone { Id = job.Id, FileNum = AllFilesDone } } }, MessagePriority.Bulk, ct).ConfigureAwait(false);
            try
            {
                // Done means the receiver has it, not merely that we pushed it into the socket.
                await job.Acknowledged.Task.WaitAsync(AckTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogWarning("Job {Id}: receiver did not acknowledge completion", job.Id);
            }

            Finish(job, TransferState.Done, null);
        }
        catch (OperationCanceledException)
        {
            if (job.State == TransferState.Running)
            {
                Finish(job, TransferState.Cancelled, "cancelled");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PathGuardException)
        {
            await SendErrorAsync(job.Id, job.FileIndex, e.Message, CancellationToken.None).ConfigureAwait(false);
            Finish(job, TransferState.Failed, e.Message);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---- receiving side ----

    private async ValueTask ConfirmDownloadFileAsync(FileTransferDigest digest, CancellationToken ct)
    {
        if (!_receiveJobs.TryGetValue(digest.Id, out TransferJob? job) || digest.FileNum >= job.Files.Count)
        {
            return;
        }

        job.FileIndex = digest.FileNum;
        TransferFile incoming = job.Files[digest.FileNum] with { Size = (long)digest.FileSize, Modified = DateTimeOffset.FromUnixTimeSeconds((long)digest.LastModified) };
        job.Files[digest.FileNum] = incoming;
        await DecideAndConfirmAsync(job, incoming, ct).ConfigureAwait(false);
    }

    private async Task ConfirmUploadFileAsync(TransferJob job, CancellationToken ct)
    {
        if (job.FileIndex >= job.Files.Count)
        {
            return;
        }

        await DecideAndConfirmAsync(job, job.Files[job.FileIndex], ct).ConfigureAwait(false);
    }

    private async ValueTask DecideAndConfirmAsync(TransferJob job, TransferFile incoming, CancellationToken ct)
    {
        string target = PathGuard.Join(_fs, job.LocalPath, incoming.RelativePath);
        string partial = target + PartialSuffix;
        long partialBytes = _fs.Stat(partial)?.Size ?? 0;
        OverwriteDecision decision = _policy.Decide(target, _fs.Stat(target), incoming, partialBytes);
        var confirm = new FileTransferSendConfirm { Id = job.Id, FileNum = job.FileIndex };
        switch (decision)
        {
            case OverwriteDecision.Skip:
                confirm.Skip = true;
                job.TransferredBytes += incoming.Size;
                job.FileIndex++;
                break;
            case OverwriteDecision.Resume:
                uint offsetBlock = (uint)(partialBytes / ProtocolConstants.FileBlockBytes);
                confirm.OffsetBlk = offsetBlock;
                OpenTarget(job, target, partial, resumeToBlock: offsetBlock);
                break;
            default:
                confirm.OffsetBlk = 0;
                OpenTarget(job, target, partial, resumeToBlock: 0);
                break;
        }

        await _send(new Message { FileAction = new FileAction { SendConfirm = confirm } }, MessagePriority.Control, ct).ConfigureAwait(false);
        Report(job);
        if (decision == OverwriteDecision.Skip && !job.IsSending && job.RemotePath.Length == 0)
        {
            // Upload receiver: the sender waits on us for the next file.
            await ConfirmUploadFileAsync(job, ct).ConfigureAwait(false);
        }
    }

    private void OpenTarget(TransferJob job, string target, string partial, uint resumeToBlock)
    {
        job.Current?.Dispose();
        long keep = (long)resumeToBlock * ProtocolConstants.FileBlockBytes;
        Stream stream = _fs.OpenWrite(partial, append: resumeToBlock > 0);
        if (resumeToBlock > 0 && stream.CanSeek)
        {
            // Drop any bytes past the last whole block we are resuming from (a partial's tail block may be
            // incomplete), then position exactly at the resume point so the next block lands there.
            if (stream.Length > keep)
            {
                stream.SetLength(keep);
            }

            stream.Seek(keep, SeekOrigin.Begin);
        }

        job.Current = stream;
        job.CurrentPath = target;
        job.ExpectedBlock = resumeToBlock;
        job.TransferredBytes += keep;
    }

    private async ValueTask WriteBlockAsync(FileTransferBlock block, CancellationToken ct)
    {
        if (!_receiveJobs.TryGetValue(block.Id, out TransferJob? job) || job.Current is null || block.FileNum != job.FileIndex)
        {
            return;
        }

        if (block.BlkId != job.ExpectedBlock)
        {
            await SendErrorAsync(job.Id, job.FileIndex, $"Block {block.BlkId} out of order (expected {job.ExpectedBlock}).", ct).ConfigureAwait(false);
            Finish(job, TransferState.Failed, "block out of order");
            return;
        }

        await job.Current.WriteAsync(block.Data.Memory, ct).ConfigureAwait(false);
        job.ExpectedBlock++;
        job.TransferredBytes += block.Data.Length;
        ReportThrottled(job);
    }

    private async Task FinishFileAsync(int id, int fileNum)
    {
        if (!_receiveJobs.TryGetValue(id, out TransferJob? job))
        {
            return;
        }

        if (fileNum == AllFilesDone)
        {
            Finish(job, TransferState.Done, null);
            await _send(new Message { FileResponse = new FileResponse { Done = new FileTransferDone { Id = id, FileNum = AllFilesReceived } } }, MessagePriority.Control, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        if (fileNum != job.FileIndex || job.Current is null || job.CurrentPath is null)
        {
            return;
        }

        await job.Current.FlushAsync().ConfigureAwait(false);
        await job.Current.DisposeAsync().ConfigureAwait(false);
        job.Current = null;
        string partial = job.CurrentPath + PartialSuffix;
        _fs.Move(partial, job.CurrentPath);
        _fs.SetModified(job.CurrentPath, job.Files[job.FileIndex].Modified);
        job.CurrentPath = null;
        job.FileIndex++;
        Report(job);
        if (job.RemotePath.Length == 0)
        {
            await ConfirmUploadFileAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // ---- helpers ----

    private void Finish(TransferJob job, TransferState state, string? error)
    {
        if (job.State is TransferState.Done or TransferState.Cancelled or TransferState.Failed)
        {
            return;
        }

        job.State = state;
        job.Error = error;
        job.Cancellation.Cancel();
        job.CancelConfirmations();
        if (job.Current is not null)
        {
            job.Current.Dispose();
            job.Current = null;
            if (state != TransferState.Done && job.CurrentPath is not null)
            {
                try
                {
                    _fs.DeleteFile(job.CurrentPath + PartialSuffix);
                }
                catch (Exception)
                {
                }
            }
        }

        Report(job);
        switch (state)
        {
            case TransferState.Done:
                job.Completion.TrySetResult();
                break;
            case TransferState.Cancelled:
                job.Completion.TrySetCanceled();
                break;
            default:
                job.Completion.TrySetException(new FileTransferFailedException(job.Id, error ?? "transfer failed"));
                break;
        }

        RememberOutcome(job);
        (job.IsSending ? _sendJobs : _receiveJobs).TryRemove(job.Id, out _);
    }

    /// <summary>Keeps the last few hundred outcomes so a late <see cref="WaitForJobAsync"/> still tells the truth.</summary>
    private void RememberOutcome(TransferJob job)
    {
        const int Keep = 256;

        Task completion = job.Completion.Task;

        // Nobody may ever await a remembered failure, and an unobserved faulted Task is raised at collection.
        _ = completion.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        _outcomes[job.Id] = completion;
        _outcomeOrder.Enqueue(job.Id);
        while (_outcomeOrder.Count > Keep && _outcomeOrder.TryDequeue(out int old))
        {
            _outcomes.TryRemove(old, out _);
        }
    }

    private void Report(TransferJob job)
    {
        job.LastProgressTimestamp = _time.GetTimestamp();
        Progress?.Invoke(job.Snapshot(_time));
    }

    private void ReportThrottled(TransferJob job)
    {
        if (_time.GetElapsedTime(job.LastProgressTimestamp) >= TimeSpan.FromMilliseconds(250))
        {
            Report(job);
        }
    }

    private ValueTask SendErrorAsync(int id, int fileNum, string error, CancellationToken ct) =>
        _send(new Message { FileResponse = new FileResponse { Error = new FileTransferError { Id = id, FileNum = fileNum, Error = error } } }, MessagePriority.Control, ct);

    private void CheckAllowed(string path)
    {
        if (IsPathAllowed is not null && !IsPathAllowed(path))
        {
            throw new UnauthorizedAccessException("Access to this path is not allowed.");
        }
    }

    private static FileEntry ToEntry(TransferFile f) => new()
    {
        Type = FileType.FtFile,
        Name = f.RelativePath,
        Size = (ulong)f.Size,
        ModifiedUnix = (ulong)Math.Max(0, f.Modified.ToUnixTimeSeconds()),
    };

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (TransferJob job in _sendJobs.Values.Concat(_receiveJobs.Values).ToList())
        {
            Finish(job, TransferState.Cancelled, "session closed");
        }

        _cts.Dispose();
    }
}
