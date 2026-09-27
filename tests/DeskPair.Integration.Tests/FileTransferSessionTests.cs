using System.Security.Cryptography;
using DeskPair.Core.FileTransfer;
using DeskPair.Core.Session.Controller;
using DeskPair.Core.Session.Host;
using DeskPair.Core.Transport;
using DeskPair.Protocol.Messages;
using DeskPair.Protocol.Rendezvous;

namespace DeskPair.Integration.Tests;

public class FileTransferSessionTests
{
    [Fact]
    public async Task Controller_downloads_and_uploads_through_a_relayed_session()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(files: true);
        (ControllerSession session, _, PeerConnector connector) = bed.CreateController(connType: ConnType.ConnFileTransfer);
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        string hostDir = bed.NewTempDir();
        string localDir = bed.NewTempDir();
        byte[] payload = new byte[3 * 1024 * 1024 + 123];
        RandomNumberGenerator.Fill(payload);
        Directory.CreateDirectory(Path.Combine(hostDir, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(hostDir, "nested", "data.bin"), payload);
        await File.WriteAllTextAsync(Path.Combine(hostDir, "readme.txt"), "hello");

        FileDirectory listing = await session.Files.ListDirectoryAsync(hostDir, false, CancellationToken.None);
        listing.Entries.Select(e => e.Name).OrderBy(n => n).ShouldBe(["nested", "readme.txt"]);

        var snapshots = new List<TransferJobSnapshot>();
        session.Files.Progress += s =>
        {
            lock (snapshots)
            {
                snapshots.Add(s);
            }
        };

        int download = await session.Files.StartDownloadAsync(hostDir, localDir, false, CancellationToken.None);
        await session.Files.WaitForJobAsync(download, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        (await File.ReadAllBytesAsync(Path.Combine(localDir, "nested", "data.bin"))).ShouldBe(payload);
        (await File.ReadAllTextAsync(Path.Combine(localDir, "readme.txt"))).ShouldBe("hello");
        lock (snapshots)
        {
            snapshots.Last(s => s.Id == download).State.ShouldBe(TransferState.Done);
        }

        string uploadTarget = Path.Combine(hostDir, "uploaded");
        int upload = await session.Files.StartUploadAsync(Path.Combine(localDir, "nested", "data.bin"), uploadTarget, false, CancellationToken.None);
        await session.Files.WaitForJobAsync(upload, new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);
        (await File.ReadAllBytesAsync(Path.Combine(uploadTarget, "data.bin"))).ShouldBe(payload);
    }

    [Fact]
    public async Task File_actions_are_refused_without_file_permission()
    {
        await using Testbed bed = await Testbed.StartAsync();
        (HostRuntime host, var passwords, _) = await bed.StartHostAsync(new HostPolicy { FileTransferEnabled = false }, files: true);
        (ControllerSession session, TestCallbacks cb, PeerConnector connector) = bed.CreateController();
        await session.ConnectAsync(connector, host.Identity.Id, CancellationToken.None);
        (await session.LoginAsync(passwords.TemporaryPassword, CancellationToken.None)).Success.ShouldBeTrue();

        Task<FileDirectory> listing = session.Files.ListDirectoryAsync(bed.NewTempDir(), false, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        await Testbed.WaitUntilAsync(() => cb.CloseReason is not null, "scope violation closes the session");
        await Should.ThrowAsync<OperationCanceledException>(() => listing);
    }
}
