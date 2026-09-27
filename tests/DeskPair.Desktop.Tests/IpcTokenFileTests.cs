using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Engine;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Where the engine leaves the token everything else needs to attach to it.
///
/// It used to be written into the data directory, unconditionally, at start-up. That holds while the
/// engine owns that directory and stops holding the moment a supervisor drops it to another user: the
/// data directory is then root's, at mode 0700, and the write throws before the engine has finished
/// starting. A supervisor restarts what it started, so the result is not a crash but a loop, several
/// seconds apart, with the reason in a log nobody thought to open. Windows has already produced one of
/// those, from a different cause and with exactly this shape.
/// </summary>
public class IpcTokenFileTests
{
    private static string Scratch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "deskpair-token-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task The_token_goes_where_it_was_told_to_go()
    {
        string dir = Scratch();
        try
        {
            string path = Path.Combine(dir, "runtime", "ipc.token");

            await ServerRole.WriteTokenAsync(path, "ABCD1234", NullLogger.Instance, CancellationToken.None);

            File.ReadAllText(path).ShouldBe("ABCD1234");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A token left behind by a previous engine must be replaced, not appended to or left alone: whoever
    /// reads a stale one attaches to nothing and reports the host as unreachable.
    /// </summary>
    [Fact]
    public async Task A_token_already_there_is_replaced()
    {
        string dir = Scratch();
        try
        {
            string path = Path.Combine(dir, "ipc.token");
            await File.WriteAllTextAsync(path, "0000000000000000");

            await ServerRole.WriteTokenAsync(path, "FFFF", NullLogger.Instance, CancellationToken.None);

            File.ReadAllText(path).ShouldBe("FFFF");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The token authorises every IPC client, so it is the engine's own to read and nobody else's. On
    /// Windows the directory's access control does that job instead.
    /// </summary>
    [Fact]
    public async Task Nobody_else_on_the_machine_can_read_it()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string dir = Scratch();
        try
        {
            string path = Path.Combine(dir, "ipc.token");
            await ServerRole.WriteTokenAsync(path, "ABCD", NullLogger.Instance, CancellationToken.None);

            File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A path that cannot be written is a reason to say so, not a reason to stop. The engine is running
    /// and can be reached by anyone who has the token another way; throwing here hands a supervisor a
    /// process to restart every few seconds instead.
    /// </summary>
    [Fact]
    public async Task A_path_that_cannot_be_written_does_not_take_the_engine_down()
    {
        string dir = Scratch();
        try
        {
            // A directory where a file is wanted: unwritable for a reason no platform disagrees about.
            string path = Path.Combine(dir, "occupied");
            Directory.CreateDirectory(path);

            await ServerRole.WriteTokenAsync(path, "ABCD", NullLogger.Instance, CancellationToken.None);

            Directory.Exists(path).ShouldBeTrue("the engine must not have deleted what was in its way");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
