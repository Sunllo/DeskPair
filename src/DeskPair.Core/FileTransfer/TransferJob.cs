namespace DeskPair.Core.FileTransfer;

public enum TransferState
{
    Pending,
    Running,
    Done,
    Cancelled,
    Failed,
}

public sealed record TransferFile(string RelativePath, long Size, DateTimeOffset Modified);

public sealed record TransferJobSnapshot(
    int Id,
    bool IsSending,
    string RemotePath,
    string LocalPath,
    int FileIndex,
    int TotalFiles,
    long TotalBytes,
    long TransferredBytes,
    TransferState State,
    string? CurrentFile,
    string? Error,
    double BytesPerSecond);

/// <summary>Mutable per-job state; the engine owns all access.</summary>
internal sealed class TransferJob
{
    public required int Id { get; init; }
    public required bool IsSending { get; init; }
    public required string RemotePath { get; init; }
    public required string LocalPath { get; init; }
    public required bool IncludeHidden { get; init; }
    public List<TransferFile> Files { get; } = [];
    public int FileIndex { get; set; }
    public long TotalBytes { get; set; }
    public long TransferredBytes { get; set; }
    public TransferState State { get; set; } = TransferState.Pending;
    public string? Error { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<(bool Skip, uint OffsetBlock)>> _confirmations = new();
    public Stream? Current { get; set; }
    public string? CurrentPath { get; set; }
    public uint ExpectedBlock { get; set; }
    public long StartedTimestamp { get; set; }
    public long LastProgressTimestamp { get; set; }
    public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Set when the receiver reports that every file has been written (sender side).</summary>
    public TaskCompletionSource Acknowledged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The peer's go-ahead for a file, keyed by index so it may arrive before the sender waits for it.</summary>
    public TaskCompletionSource<(bool Skip, uint OffsetBlock)> Confirmation(int fileNum) =>
        _confirmations.GetOrAdd(fileNum, _ => new TaskCompletionSource<(bool Skip, uint OffsetBlock)>(TaskCreationOptions.RunContinuationsAsynchronously));

    public void CancelConfirmations()
    {
        foreach (TaskCompletionSource<(bool Skip, uint OffsetBlock)> tcs in _confirmations.Values)
        {
            tcs.TrySetCanceled();
        }
    }

    public TransferJobSnapshot Snapshot(TimeProvider time) => new(
        Id, IsSending, RemotePath, LocalPath, FileIndex, Files.Count, TotalBytes, TransferredBytes, State,
        FileIndex < Files.Count ? Files[FileIndex].RelativePath : null, Error,
        StartedTimestamp == 0 ? 0 : TransferredBytes / Math.Max(0.001, time.GetElapsedTime(StartedTimestamp).TotalSeconds));
}
