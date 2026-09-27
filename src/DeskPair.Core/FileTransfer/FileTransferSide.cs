namespace DeskPair.Core.FileTransfer;

/// <summary>
/// Which end of a session an engine belongs to. It decides nothing but the range job ids are drawn from,
/// and that only matters because both ends can start transfers once pasting works in both directions.
/// </summary>
public enum FileTransferSide
{
    Controller,
    Host,
}

/// <summary>A job ended in <see cref="TransferState.Failed"/>; the message is what the peer reported.</summary>
public sealed class FileTransferFailedException(int jobId, string message) : IOException(message)
{
    public int JobId { get; } = jobId;
}
