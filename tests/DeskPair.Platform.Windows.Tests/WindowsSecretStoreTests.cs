using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using DeskPair.Platform.Abstractions.Security;
using DeskPair.Platform.Windows.Security;

namespace DeskPair.Platform.Windows.Tests;

/// <summary>
/// A secret that cannot be read is not a secret that is not there.
///
/// This distinction cost a real host its identity. The store returned null for a blob it could not
/// decrypt, null means "make one", and the caller that made one wrote it over the original -- the
/// identity key, the password salt and the peer id of a machine that other devices had trusted, gone in
/// the same second, from a LocalSystem engine started beside a signed-in user's app against the same
/// directory. None of it was recoverable.
/// </summary>
public class WindowsSecretStoreTests
{
    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "deskpair-secrets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public async Task A_secret_that_is_not_there_is_null()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // DPAPI is a Windows arrangement
        }

        string directory = NewDirectory();
        try
        {
            var store = new WindowsSecretStore(directory, machineScope: false);
            (await store.GetAsync("nothing-here")).ShouldBeNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task A_secret_written_here_comes_back()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = NewDirectory();
        try
        {
            var store = new WindowsSecretStore(directory, machineScope: false);
            await store.SetAsync("identity-key", new byte[] { 1, 2, 3, 4 });

            (await store.GetAsync("identity-key")).ShouldBe([1, 2, 3, 4]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The machine scope and the user scope have to be able to read each other's work, because a store
    /// outlives the arrangement that wrote it: the app writes one, the service's engine reads the other.
    /// </summary>
    [Fact]
    public async Task Either_scope_reads_what_the_other_wrote()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = NewDirectory();
        try
        {
            await new WindowsSecretStore(directory, machineScope: false).SetAsync("a", new byte[] { 9 });
            await new WindowsSecretStore(directory, machineScope: true).SetAsync("b", new byte[] { 8 });

            (await new WindowsSecretStore(directory, machineScope: true).GetAsync("a")).ShouldBe([9]);
            (await new WindowsSecretStore(directory, machineScope: false).GetAsync("b")).ShouldBe([8]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// DPAPI's machine scope has no wrong key -- anything on the computer that can open the file can
    /// decrypt it -- so the file permissions have to carry what the encryption no longer does. Under
    /// ProgramData the Users group is granted read by inheritance, which would leave a standard account
    /// able to lift this machine's identity key.
    /// </summary>
    [Fact]
    public void The_directory_is_not_readable_by_every_account_on_the_machine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = NewDirectory();
        try
        {
            // Put the thing being guarded against there first.
            //
            // Without this the assertion passes on any machine whose temp directory happens not to grant
            // the Users group -- which is every machine, so it passed while the code it was meant to
            // check did nothing at all. ProgramData does grant it, which is where this actually runs.
            var info = new DirectoryInfo(directory);
            DirectorySecurity before = info.GetAccessControl();
            before.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute,
                InheritanceFlags.ObjectInherit | InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            info.SetAccessControl(before);

            _ = new WindowsSecretStore(directory, machineScope: true);

            AuthorizationRuleCollection rules = new DirectoryInfo(directory)
                .GetAccessControl()
                .GetAccessRules(true, true, typeof(SecurityIdentifier));

            var identities = rules.Cast<FileSystemAccessRule>()
                .Select(r => (SecurityIdentifier)r.IdentityReference)
                .ToList();

            identities.ShouldNotContain(s => s.IsWellKnown(WellKnownSidType.BuiltinUsersSid));
            identities.ShouldNotContain(s => s.IsWellKnown(WellKnownSidType.WorldSid));

            // And the accounts that do have business here are still there -- an unreadable secret
            // directory is the other way to lose everything in it.
            identities.ShouldContain(s => s.IsWellKnown(WellKnownSidType.LocalSystemSid));
            identities.ShouldContain(s => s.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Locking the directory down must not lock out the account doing the locking: this runs as an
    /// ordinary user, writes, and reads back.
    /// </summary>
    [Fact]
    public async Task The_account_that_restricted_it_can_still_use_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = NewDirectory();
        try
        {
            var store = new WindowsSecretStore(directory, machineScope: true);
            await store.SetAsync("identity-key", new byte[] { 5, 6 });

            (await new WindowsSecretStore(directory, machineScope: true).GetAsync("identity-key"))
                .ShouldBe([5, 6]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Reading tells you nothing about which scope a secret was written under.
    ///
    /// DPAPI's Unprotect ignores the scope it is handed and works out which master key a blob needs from
    /// the blob, so a machine-scoped store reads a user-scoped secret back without complaint, as long as
    /// it is that user asking. Surprising enough to be worth pinning, because it is what made
    /// "has this been handed over to the machine scope yet" an unanswerable question -- and a migration
    /// that asked it concluded there was nothing to do, leaving the service's engine unable to read
    /// anything and restarting every six seconds for as long as it was installed.
    /// </summary>
    [Fact]
    public async Task The_scope_a_secret_was_written_under_cannot_be_told_by_reading_it()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = NewDirectory();
        try
        {
            await new WindowsSecretStore(directory, machineScope: false).SetAsync("identity-key", new byte[] { 3 });

            (await new WindowsSecretStore(directory, machineScope: true).GetAsync("identity-key")).ShouldBe([3]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The one that matters. A file that is neither scope's -- another account's, or this machine before
    /// it was reinstalled -- must stop the caller rather than invite it to write a replacement.
    /// </summary>
    [Fact]
    public async Task A_secret_belonging_to_somebody_else_is_refused_rather_than_replaced()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = NewDirectory();
        try
        {
            // Protected with different entropy, which is the same situation as another account's DPAPI
            // key from this side: the bytes are there and they will not come out.
            byte[] theirs = ProtectedData.Protect([7, 7, 7], [0xAA, 0xBB], DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(Path.Combine(directory, "identity-key.dpapi"), theirs);

            var store = new WindowsSecretStore(directory, machineScope: false);

            SecretUnreadableException refused = await Should.ThrowAsync<SecretUnreadableException>(
                async () => await store.GetAsync("identity-key"));
            refused.Key.ShouldBe("identity-key");

            // And still there afterwards. The point is not the exception, it is that nothing was lost.
            (await File.ReadAllBytesAsync(Path.Combine(directory, "identity-key.dpapi"))).ShouldBe(theirs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
