using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Platform.Windows.Security;

/// <summary>
/// DPAPI-protected files.
///
/// The scope decides who can read them back. A user-scoped secret is unreadable by LocalSystem and the
/// other way round, so any store that two accounts share has to be machine-scoped -- which is what the
/// host service needs, since its engine runs as LocalSystem against the same directory the app uses.
///
/// The scope only applies to writing. Unprotect works out which master key a blob needs from the blob, so
/// a store set to either scope reads back whatever this account can decrypt -- which is why moving secrets
/// between scopes is a matter of rewriting them, not of reading them differently.
///
/// What is never done is to mistake a secret for a missing one: see <see cref="SecretUnreadableException"/>.
/// </summary>
public sealed class WindowsSecretStore : ISecretStore
{
    private readonly string _directory;
    private readonly DataProtectionScope _scope;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DeskPair.v1");

    /// <param name="directory">Where the protected files live.</param>
    /// <param name="machineScope">
    /// Which DPAPI scope to protect with. It has no effect on reading: Unprotect works out which master
    /// key a blob needs from the blob, and ignores the scope it is handed. A machine-scoped store will
    /// therefore read a user-scoped secret back quite happily, so long as it is that user asking.
    /// </param>
    public WindowsSecretStore(string directory, bool machineScope)
    {
        _directory = directory;
        _scope = machineScope ? DataProtectionScope.LocalMachine : DataProtectionScope.CurrentUser;
        Directory.CreateDirectory(directory);
        AccessControlError = Restrict(directory);
    }

    /// <summary>
    /// Why the directory could not be restricted to the accounts that belong in it, or null when it was.
    /// The store still works either way -- refusing to start over an access-control rule would be a worse
    /// outcome than the one it guards against -- but the caller is expected to log this, because a
    /// secrets directory the Users group can read is a fault somebody has to hear about.
    /// </summary>
    public string? AccessControlError { get; }

    /// <summary>
    /// Keeps the directory to the accounts that have business in it: LocalSystem, administrators, and
    /// whoever is running.
    ///
    /// DPAPI protects the contents from the wrong key, not from the wrong reader, and the machine scope
    /// has no wrong key -- anything on this computer that can open the file can decrypt it. The host
    /// service needs that scope, because its engine is LocalSystem and the app is not, and they are meant
    /// to be one host with one identity. So the file permissions have to carry what the encryption no
    /// longer does. This lives under ProgramData, where the Users group is granted read by default, which
    /// would have left a standard account able to lift this machine's identity key.
    ///
    /// Existing explicit entries are left alone, and only inherited ones are dropped. That is what lets
    /// the app keep its own access after the service has been installed and LocalSystem has been here:
    /// removing what it did not add would lock the owner out of their own secrets, which is the failure
    /// this whole area has already produced once.
    /// </summary>
    private static string? Restrict(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);

            // Two passes, and it has to be two.
            //
            // Turning inheritance off with preserveInheritance copies the inherited rules in as explicit
            // ones -- but only when the descriptor is applied. Until then they are still flagged
            // inherited, and an inherited rule cannot be removed; RemoveAccessRuleAll silently does
            // nothing to it. Doing both in one pass looks right, applies cleanly, and leaves the Users
            // group exactly where it was.
            DirectorySecurity detach = info.GetAccessControl();
            detach.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            info.SetAccessControl(detach);

            DirectorySecurity security = info.GetAccessControl();
            foreach (FileSystemAccessRule rule in security
                         .GetAccessRules(true, true, typeof(SecurityIdentifier))
                         .Cast<FileSystemAccessRule>()
                         .Where(r => Unwanted(r.IdentityReference as SecurityIdentifier))
                         .ToList())
            {
                security.RemoveAccessRuleAll(rule);
            }

            foreach (SecurityIdentifier who in Wanted())
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    who,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }

            info.SetAccessControl(security);
            return null;
        }
        catch (Exception e)
        {
            // Not fatal, see AccessControlError. A store with the permissions it already had still works.
            return e.Message;
        }
    }

    /// <summary>The broad groups: everyone on the machine, and every ordinary account on it.</summary>
    private static bool Unwanted(SecurityIdentifier? who) =>
        who is not null
        && (who.IsWellKnown(WellKnownSidType.BuiltinUsersSid)
            || who.IsWellKnown(WellKnownSidType.WorldSid)
            || who.IsWellKnown(WellKnownSidType.AuthenticatedUserSid)
            || who.IsWellKnown(WellKnownSidType.InteractiveSid));

    private static IEnumerable<SecurityIdentifier> Wanted()
    {
        yield return new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        yield return new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        SecurityIdentifier? me = null;
        try
        {
            using WindowsIdentity current = WindowsIdentity.GetCurrent();
            me = current.User;
        }
        catch (Exception)
        {
        }

        if (me is not null)
        {
            yield return me;
        }
    }

    public async ValueTask<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        string path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] protectedBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        try
        {
            // The scope is not consulted here, whatever it is set to. Unprotect reads which master key a
            // blob needs out of the blob itself; this argument only ever mattered to Protect. An earlier
            // version tried the other scope after this one failed, which could never have helped -- the
            // first call had already tried every key available to this process.
            return ProtectedData.Unprotect(protectedBytes, Entropy, _scope);
        }
        catch (CryptographicException e)
        {
            // Neither scope. Something is here and it is not ours -- another user's, or this machine
            // before it was reinstalled.
            //
            // This used to return null, and null means "there is none", and a caller told there is none
            // writes a replacement. That is how a LocalSystem engine, started beside a signed-in user's
            // app against the same directory, overwrote three of four secrets and cost a host the
            // identity it had been reachable by. Refusing is the only safe answer: a secret that cannot
            // be read can still be somebody's.
            throw new SecretUnreadableException(key, e.Message);
        }
    }

    public async ValueTask SetAsync(string key, ReadOnlyMemory<byte> value, CancellationToken ct = default)
    {
        byte[] protectedBytes = ProtectedData.Protect(value.ToArray(), Entropy, _scope);
        string path = PathFor(key);
        string tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, protectedBytes, ct).ConfigureAwait(false);
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

        return Path.Combine(_directory, key + ".dpapi");
    }
}

/// <summary>Stable per-machine id from the Cryptography MachineGuid, hashed so the raw GUID is never sent.</summary>
public sealed class WindowsMachineIdProvider : IMachineIdProvider
{
    private readonly Lazy<byte[]> _id = new(Compute);

    public byte[] GetStableMachineId() => (byte[])_id.Value.Clone();

    private static byte[] Compute()
    {
        string? guid = null;
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            guid = key?.GetValue("MachineGuid") as string;
        }
        catch (Exception)
        {
        }

        guid ??= Environment.MachineName;
        return SHA256.HashData(Encoding.UTF8.GetBytes("DeskPair:" + guid));
    }
}
