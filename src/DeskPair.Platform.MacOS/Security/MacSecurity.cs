using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.MacOS.Native;

namespace DeskPair.Platform.MacOS.Security;

/// <summary>The hardware UUID (IOPlatformUUID), which is stable across reinstalls and unique per machine.</summary>
public sealed class MacMachineIdProvider : IMachineIdProvider
{
    public byte[] GetStableMachineId()
    {
        var buf = new byte[64];
        int len = MacShim.fd_machine_uuid(buf, buf.Length);
        if (len > 0)
        {
            return buf[..len]; // the UTF-8 bytes of the UUID string
        }

        // No IOPlatformUUID (unexpected on real hardware); a random id keeps the app working, just not stable.
        return Guid.NewGuid().ToByteArray();
    }
}

/// <summary>
/// Small secrets in the login keychain (generic passwords under a fixed service). The identity key and
/// password hashes live here, protected by the keychain rather than a plain file. Values are stored as raw
/// bytes; the account name is the caller's key.
/// </summary>
public sealed class MacSecretStore : ISecretStore
{
    /// <summary>
    /// Whether this store can work at all: every call goes through the shim, so without it the store is
    /// a DllNotFoundException waiting for its first read rather than a store. Callers choosing between
    /// this and a file store need to ask before constructing, not after.
    /// </summary>
    public static bool IsAvailable => MacShim.IsAvailable;

    /// <summary>errSecSuccess. The keychain says nothing went wrong.</summary>
    private const int Success = 0;

    /// <summary>errSecItemNotFound. Nothing is stored here, which is the one honest reason to return null.</summary>
    private const int NotFound = -25300;

    /// <summary>
    /// Reads an item an earlier name of this product left in the keychain.
    ///
    /// The product was FastDesk and kept its items under "Sunllo FastDesk". Nothing reads that any more,
    /// which means an upgrade produces a machine with a new identity key, a new id at the rendezvous
    /// server and a new password -- and every device that had trusted the old one simply stops
    /// recognising it, with no error anywhere. This is the one call that can see across that line.
    ///
    /// Same rule as everywhere else in this file: nothing there is null, anything else throws. A secret
    /// that exists and will not come out must never be mistaken for one that was never written.
    /// </summary>
    public static byte[]? ReadLegacy(string service, string key)
    {
        int status = MacShim.fd_keychain_get_from(service, key, out nint data, out int len);
        if (status == NotFound)
        {
            return null;
        }

        if (status != Success || data == 0)
        {
            throw new SecretUnreadableException(key, $"the keychain answered {status} for service \"{service}\"");
        }

        try
        {
            var result = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(data, result, 0, len);
            return result;
        }
        finally
        {
            MacShim.fd_free(data);
        }
    }

    public ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        int status = MacShim.fd_keychain_get(key, out nint data, out int len);
        if (status == NotFound)
        {
            return ValueTask.FromResult<byte[]?>(null);
        }

        // Anything else is a secret that exists and will not come out: a keychain prompt somebody
        // declined, or an item whose access list no longer names this binary after it was signed again.
        //
        // This used to be folded in with "not found", and null means "make one", and the caller that
        // makes one writes it over the original. That is how the Windows side of this cost a host its
        // identity key, its password salt and its peer id in the same second, none of it recoverable.
        // A keychain is worse, not better: it asks, and a person clicking Deny is an ordinary thing.
        if (status != Success || data == 0)
        {
            throw new SecretUnreadableException(key, $"the keychain answered {status}");
        }

        try
        {
            var result = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(data, result, 0, len);
            return ValueTask.FromResult<byte[]?>(result);
        }
        finally
        {
            MacShim.fd_free(data);
        }
    }

    public unsafe ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
    {
        fixed (byte* p = value.Span)
        {
            MacShim.fd_keychain_set(key, p, value.Length);
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string key, CancellationToken ct = default)
    {
        MacShim.fd_keychain_delete(key);
        return ValueTask.CompletedTask;
    }
}
