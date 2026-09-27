using DeskPair.Core.FileTransfer;

namespace DeskPair.Core.Tests;

/// <summary>
/// Exercises the real <see cref="LocalFileSystem"/> against an actual temp directory. The file-transfer
/// engine's resume path is covered elsewhere against an in-memory file system, whose stream happens to allow
/// truncation while positioned at the end — the real <see cref="FileStream"/> does not, so a resume that
/// opened the partial in append mode threw "Unable to truncate ... a file opened in Append mode" only in
/// production. These tests pin the behaviour the engine relies on.
/// </summary>
public sealed class LocalFileSystemTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fd-lfs-" + Guid.NewGuid().ToString("N"));

    public LocalFileSystemTests() => Directory.CreateDirectory(_dir);

    [Fact]
    public async Task OpenWrite_append_allows_truncating_and_appending_a_partial()
    {
        // Reproduce exactly what FileTransferEngine.OpenTarget does when resuming: open an existing partial
        // that has an incomplete tail block, trim it back to the last whole block, seek there, and append.
        string path = Path.Combine(_dir, "resume.part");
        byte[] seeded = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(path, seeded);

        const long keep = 2048;
        byte[] tail = Enumerable.Range(0, 1000).Select(i => (byte)(255 - (i & 0xFF))).ToArray();

        LocalFileSystem fs = LocalFileSystem.Instance;
        await using (Stream stream = fs.OpenWrite(path, append: true))
        {
            stream.CanSeek.ShouldBeTrue();
            stream.SetLength(keep); // must not throw for a resumable partial
            stream.Seek(keep, SeekOrigin.Begin);
            await stream.WriteAsync(tail);
        }

        byte[] result = await File.ReadAllBytesAsync(path);
        result.Length.ShouldBe((int)keep + tail.Length);
        result[..(int)keep].ShouldBe(seeded[..(int)keep]); // kept block survived
        result[(int)keep..].ShouldBe(tail);                 // appended tail is exact
    }

    [Fact]
    public async Task OpenWrite_without_append_truncates_an_existing_file()
    {
        string path = Path.Combine(_dir, "fresh.bin");
        await File.WriteAllBytesAsync(path, new byte[5000]);

        byte[] payload = Enumerable.Range(0, 1234).Select(i => (byte)i).ToArray();
        LocalFileSystem fs = LocalFileSystem.Instance;
        await using (Stream stream = fs.OpenWrite(path, append: false))
        {
            await stream.WriteAsync(payload);
        }

        (await File.ReadAllBytesAsync(path)).ShouldBe(payload);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
