using System.Security.Cryptography;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Core.Config;

/// <summary>
/// Plain-file secret store, used where no OS keystore fits: tools, tests, Linux, and the machine-wide
/// store macOS needs once a job runs as root at the login window and as the user in Aqua -- no keychain
/// can serve both, because the login keychain is per-user and the System keychain is root's alone.
///
/// Files are created owner-only on Unix. That is weaker at rest than a keychain, which is encrypted with
/// the login password, and it is the price of being readable before anybody has typed that password.
/// </summary>
public sealed class FileSecretStore : ISecretStore
{
    private readonly string _directory;

    public FileSecretStore(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Reads the secret, or says it cannot -- and never confuses the two.
    ///
    /// This used to ask File.Exists first, which is the whole bug in one call: File.Exists answers false
    /// for a file it is not allowed to look at, so a store belonging to another account read as an empty
    /// one. Empty means "make a new key and save it", and saving it over somebody else's is how a machine
    /// loses the identity it was reachable by. That has already happened once on Windows, to a real host,
    /// and the arrangement being built here -- a root daemon and a signed-in user sharing one directory --
    /// is precisely the one that produces it.
    ///
    /// So the file is opened, and the answer comes from what the open says. Missing is missing; refused is
    /// refused, and refused is worth stopping for.
    /// </summary>
    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        string path = PathFor(key);
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException e)
        {
            throw new SecretUnreadableException(key, e.Message);
        }
        catch (IOException e)
        {
            // A directory this process may not traverse surfaces here rather than as an access error, and
            // it means the same thing: there may be something under this key, and it is not ours to read.
            throw new SecretUnreadableException(key, e.Message);
        }
    }

    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
    {
        string path = PathFor(key);
        string tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, value.ToArray(), ct).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(tmp, path, overwrite: true);
    }

    public ValueTask RemoveAsync(string key, CancellationToken ct = default)
    {
        File.Delete(PathFor(key));
        return ValueTask.CompletedTask;
    }

    private string PathFor(string key)
    {
        if (key.Length == 0 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
        {
            throw new ArgumentException("Secret keys must be alphanumeric.", nameof(key));
        }

        return Path.Combine(_directory, key + ".bin");
    }
}

/// <summary>Random machine id persisted in the secret store; used until platform-specific providers exist.</summary>
public sealed class StoredMachineIdProvider : IMachineIdProvider
{
    private const string Key = "machine-id";
    private readonly byte[] _id;

    private StoredMachineIdProvider(byte[] id)
    {
        _id = id;
    }

    public static async Task<StoredMachineIdProvider> LoadOrCreateAsync(ISecretStore store, CancellationToken ct = default)
    {
        byte[]? id = await store.GetAsync(Key, ct).ConfigureAwait(false);
        if (id is null || id.Length != 32)
        {
            id = RandomNumberGenerator.GetBytes(32);
            await store.SetAsync(Key, id, ct).ConfigureAwait(false);
        }

        return new StoredMachineIdProvider(id);
    }

    public byte[] GetStableMachineId() => (byte[])_id.Clone();
}
