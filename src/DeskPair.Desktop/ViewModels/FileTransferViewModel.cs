using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using DeskPair.Core.FileTransfer;
using DeskPair.Core.Session.Controller;
using DeskPair.Desktop.Services;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Desktop.ViewModels;

public sealed partial class FileRow : ObservableObject
{
    public FileRow(string name, bool isDirectory, long size, DateTimeOffset modified)
    {
        Name = name;
        IsDirectory = isDirectory;
        Size = size;
        Modified = modified;
    }

    public string Name { get; }

    public bool IsDirectory { get; }

    public long Size { get; }

    public DateTimeOffset Modified { get; }

    public string Icon => IsDirectory ? "📁" : "📄";

    public string SizeText => IsDirectory ? string.Empty : FormatSize(Size);

    public string ModifiedText => Modified == default ? string.Empty : Modified.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}

public sealed partial class TransferRow : ObservableObject
{
    public TransferRow(int id)
    {
        Id = id;
        Text = string.Empty;
    }

    public int Id { get; }

    [ObservableProperty]
    public partial string Text { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    public void Update(TransferJobSnapshot s)
    {
        Progress = s.TotalBytes > 0 ? 100.0 * s.TransferredBytes / s.TotalBytes : 0;
        IsActive = s.State is TransferState.Pending or TransferState.Running;
        string what = s.IsSending ? $"→ {s.RemotePath}" : $"← {s.LocalPath}";
        string state = s.State switch
        {
            TransferState.Running => $"{FileRow.FormatSize(s.TransferredBytes)} / {FileRow.FormatSize(s.TotalBytes)} ({FileRow.FormatSize((long)s.BytesPerSecond)}/s)",
            TransferState.Failed => $"failed: {s.Error}",
            _ => s.State.ToString().ToLowerInvariant(),
        };
        Text = $"{what}  {s.FileIndex + 1}/{s.TotalFiles}  {state}";
    }
}

/// <summary>Two-pane file browser over a file-transfer session.</summary>
public partial class FileTransferViewModel : SessionViewModelBase
{
    private readonly IFileSystem _local = LocalFileSystem.Instance;

    public FileTransferViewModel(string target, string myId, DesktopConfig config, ILoggerFactory logs)
        : base(target, myId, config, logs)
    {
        string preferred = App.Config.FileTransferFolder;
        LocalPath = preferred.Length > 0 && Directory.Exists(preferred)
            ? preferred
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        RemotePath = string.Empty;
        LocalError = string.Empty;
        RemoteError = string.Empty;
        RefreshLocal();
    }

    protected override ConnType ConnType => ConnType.ConnFileTransfer;

    public ObservableCollection<FileRow> LocalEntries { get; } = [];

    public ObservableCollection<FileRow> RemoteEntries { get; } = [];

    public ObservableCollection<TransferRow> Jobs { get; } = [];

    [ObservableProperty]
    public partial string LocalPath { get; set; }

    [ObservableProperty]
    public partial string RemotePath { get; set; }

    [ObservableProperty]
    public partial FileRow? SelectedLocal { get; set; }

    [ObservableProperty]
    public partial FileRow? SelectedRemote { get; set; }

    [ObservableProperty]
    public partial string LocalError { get; set; }

    [ObservableProperty]
    public partial string RemoteError { get; set; }

    protected override ControllerSessionOptions ConfigureOptions(ControllerSessionOptions options) => options with { FileSystem = _local };

    protected override async Task OnAuthorizedAsync()
    {
        Session!.Files.Progress += OnProgress;
        Session.Files.RemoteError += (job, path, error) => Dispatcher.UIThread.Post(() => RemoteError = $"{path}: {error}");
        await RefreshRemoteAsync();
    }

    // ---- local pane ----

    [RelayCommand]
    private void RefreshLocal()
    {
        LocalEntries.Clear();
        LocalError = string.Empty;
        try
        {
            if (LocalPath.Length == 0 && OperatingSystem.IsWindows())
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                {
                    LocalEntries.Add(new FileRow(drive.Name, true, 0, default));
                }

                return;
            }

            foreach (FsEntry entry in _local.ListDirectory(LocalPath, includeHidden: false).OrderByDescending(e => e.Kind is FsEntryKind.Directory or FsEntryKind.Drive).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                LocalEntries.Add(new FileRow(entry.Name, entry.Kind is FsEntryKind.Directory or FsEntryKind.Drive, entry.Size, entry.Modified));
            }
        }
        catch (Exception e)
        {
            LocalError = e.Message;
        }
    }

    [RelayCommand]
    private void LocalUp()
    {
        string? parent = Path.GetDirectoryName(LocalPath);
        LocalPath = parent ?? (OperatingSystem.IsWindows() ? string.Empty : "/");
        RefreshLocal();
    }

    [RelayCommand]
    private void OpenLocal(FileRow? row)
    {
        if (row is { IsDirectory: true })
        {
            LocalPath = LocalPath.Length == 0 ? row.Name : Path.Combine(LocalPath, row.Name);
            RefreshLocal();
        }
    }

    // ---- remote pane ----

    [RelayCommand]
    private async Task RefreshRemoteAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized })
        {
            return;
        }

        RemoteError = string.Empty;
        try
        {
            FileDirectory dir = await Session.Files.ListDirectoryAsync(RemotePath, includeHidden: false, Cts.Token);
            RemotePath = dir.Path;
            RemoteEntries.Clear();
            foreach (FileEntry entry in dir.Entries.OrderByDescending(e => e.Type == FileType.FtDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                RemoteEntries.Add(new FileRow(entry.Name, entry.Type == FileType.FtDir, (long)entry.Size, entry.ModifiedUnix == 0 ? default : DateTimeOffset.FromUnixTimeSeconds((long)entry.ModifiedUnix)));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            RemoteError = e.Message;
        }
    }

    [RelayCommand]
    private async Task RemoteUpAsync()
    {
        int cut = Math.Max(RemotePath.LastIndexOf('/'), RemotePath.LastIndexOf('\\'));
        RemotePath = cut > 0 ? RemotePath[..cut] : string.Empty;
        await RefreshRemoteAsync();
    }

    [RelayCommand]
    private async Task OpenRemoteAsync(FileRow? row)
    {
        if (row is { IsDirectory: true })
        {
            char sep = RemotePath.Contains('\\') ? '\\' : '/';
            RemotePath = RemotePath.Length == 0 || RemotePath.EndsWith(sep) ? RemotePath + row.Name : RemotePath + sep + row.Name;
            await RefreshRemoteAsync();
        }
    }

    [RelayCommand]
    private async Task NewRemoteFolderAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized })
        {
            return;
        }

        string name = "New folder";
        int n = 1;
        while (RemoteEntries.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"New folder ({++n})";
        }

        await Session.Files.CreateDirectoryAsync(JoinRemote(name), Cts.Token);
        await RefreshRemoteAsync();
    }

    [RelayCommand]
    private async Task DeleteRemoteAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized } || SelectedRemote is null)
        {
            return;
        }

        string path = JoinRemote(SelectedRemote.Name);
        if (SelectedRemote.IsDirectory)
        {
            await Session.Files.RemoveDirectoryAsync(path, recursive: true, Cts.Token);
        }
        else
        {
            await Session.Files.RemoveFileAsync(path, Cts.Token);
        }

        await RefreshRemoteAsync();
    }

    // ---- transfers ----

    [RelayCommand]
    private async Task SendAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized } || SelectedLocal is null)
        {
            return;
        }

        string local = LocalPath.Length == 0 ? SelectedLocal.Name : Path.Combine(LocalPath, SelectedLocal.Name);
        int id = await Session.Files.StartUploadAsync(local, RemotePath, includeHidden: false, Cts.Token);
        Track(id);
    }

    [RelayCommand]
    private async Task ReceiveAsync()
    {
        if (Session is not { State: ControllerSessionState.Authorized } || SelectedRemote is null)
        {
            return;
        }

        int id = await Session.Files.StartDownloadAsync(JoinRemote(SelectedRemote.Name), LocalPath, includeHidden: false, Cts.Token);
        Track(id);
    }

    [RelayCommand]
    private async Task CancelJobAsync(TransferRow? row)
    {
        if (row is not null && Session is not null)
        {
            await Session.Files.CancelAsync(row.Id, Cts.Token);
        }
    }

    private void Track(int id)
    {
        if (Jobs.All(j => j.Id != id))
        {
            Jobs.Insert(0, new TransferRow(id));
        }
    }

    private void OnProgress(TransferJobSnapshot snapshot) => Dispatcher.UIThread.Post(() =>
    {
        TransferRow? row = Jobs.FirstOrDefault(j => j.Id == snapshot.Id);
        if (row is null)
        {
            row = new TransferRow(snapshot.Id);
            Jobs.Insert(0, row);
        }

        row.Update(snapshot);
        if (snapshot.State == TransferState.Done)
        {
            _ = snapshot.IsSending ? RefreshRemoteAsync() : Task.Run(() => Dispatcher.UIThread.Post(RefreshLocal));
        }
    });

    private string JoinRemote(string name)
    {
        char sep = RemotePath.Contains('\\') ? '\\' : '/';
        return RemotePath.Length == 0 || RemotePath.EndsWith(sep) ? RemotePath + name : RemotePath + sep + name;
    }
}
