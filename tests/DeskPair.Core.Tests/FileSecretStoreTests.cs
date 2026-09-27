using DeskPair.Core.Config;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Core.Tests;

/// <summary>
/// The difference between "there is no secret here" and "there is one and it is not mine to read".
///
/// Those two answers lead opposite ways. Absence tells PeerIdentityStore to mint a key and save it;
/// refusal has to stop everything, because saving over somebody else's identity cannot be undone -- a real
/// host has already lost the id it was reachable by that way, on Windows, when a LocalSystem engine and a
/// signed-in user shared one directory.
///
/// The file store answered the first when it meant the second, because File.Exists returns false for a
/// file it is not allowed to look at. The arrangement being built now -- a root daemon and a signed-in
/// user sharing a machine-wide store -- is exactly the one that produces it.
/// </summary>
public class FileSecretStoreTests
{
    private static string Scratch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "deskpair-secrets-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task What_was_stored_is_what_comes_back()
    {
        string dir = Scratch();
        try
        {
            var store = new FileSecretStore(dir);
            await store.SetAsync("identity-key", new byte[] { 1, 2, 3, 4 });

            (await store.GetAsync("identity-key")).ShouldBe([1, 2, 3, 4]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_key_that_was_never_stored_is_absent_rather_than_an_error()
    {
        string dir = Scratch();
        try
        {
            (await new FileSecretStore(dir).GetAsync("never-written")).ShouldBeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// A directory that has gone away since the store was built still reads as absence: there is nothing
    /// there to overwrite, so minting a new key is the right thing and not a loss.
    /// </summary>
    [Fact]
    public async Task A_store_whose_directory_is_gone_is_absent()
    {
        string dir = Scratch();
        var store = new FileSecretStore(dir);
        Directory.Delete(dir, recursive: true);

        (await store.GetAsync("identity-key")).ShouldBeNull();
    }

    /// <summary>
    /// The one that matters: a secret that is there and cannot be read must never read as absent.
    ///
    /// Unix only, because it is the mode bits doing the refusing. On Windows the same property is enforced
    /// by the directory's access control and by DPAPI refusing another account's blob, which
    /// WindowsSecretStore already turns into this same exception.
    /// </summary>
    [Fact]
    public async Task A_secret_this_process_may_not_read_is_refused_not_reported_missing()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (Environment.GetEnvironmentVariable("USER") == "root" || Environment.IsPrivilegedProcess)
        {
            return; // root is refused nothing, so there is no refusal to observe
        }

        string dir = Scratch();
        try
        {
            var store = new FileSecretStore(dir);
            await store.SetAsync("identity-key", new byte[] { 9, 9, 9 });

            // Exactly what another account's store looks like from here.
            File.SetUnixFileMode(Path.Combine(dir, "identity-key.bin"), UnixFileMode.None);

            var refused = await Should.ThrowAsync<SecretUnreadableException>(async () => await store.GetAsync("identity-key"));
            refused.Key.ShouldBe("identity-key");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Written owner-only, because the whole point of the machine-wide store is who may read it.</summary>
    [Fact]
    public async Task Secrets_are_written_for_their_owner_alone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string dir = Scratch();
        try
        {
            await new FileSecretStore(dir).SetAsync("password-salt", new byte[] { 7 });

            File.GetUnixFileMode(Path.Combine(dir, "password-salt.bin"))
                .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
