using Microsoft.Extensions.Logging.Abstractions;
using DeskPair.Desktop.Services;

namespace DeskPair.Desktop.Tests;

/// <summary>
/// Carrying a machine's identity across the rename from FastDesk to DeskPair.
///
/// The rename moved the data directory and, on a Mac, the keychain service. Neither was migrated, and
/// neither failure looks like one: the new store is empty, empty means "mint a key and save it", and the
/// machine comes up working as a different machine -- new id at the rendezvous server, new password, and
/// every device that had trusted it no longer does.
///
/// These pin the two properties that make the migration safe rather than merely present: it fills in what
/// is missing, and it never writes over what is there.
/// </summary>
public class LegacyIdentityTests
{
    private static string Scratch()
    {
        string dir = Path.Combine(Path.GetTempPath(), "deskpair-legacy-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Write(string dir, string name, string contents)
    {
        string path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    [Fact]
    public void The_previous_name_is_the_same_place_with_the_old_name_in_it()
    {
        LegacyIdentity.OldPathFor(Path.Combine("C:", "ProgramData", "Sunllo", "DeskPair"))
            .ShouldBe(Path.Combine("C:", "ProgramData", "Sunllo", "FastDesk"));

        LegacyIdentity.OldPathFor("/Users/alice/Library/Application Support/Sunllo/DeskPair")
            .ShouldEndWith(Path.Combine("Sunllo", "FastDesk"));

        // Linux writes it lower-cased, which is a different string and the same rename.
        LegacyIdentity.OldPathFor("/home/alice/.local/share/deskpair")
            .ShouldEndWith("fastdesk");
    }

    /// <summary>
    /// Only the last segment. Somebody whose home directory happens to be called DeskPair must not have
    /// the rest of their path rewritten out from under them.
    /// </summary>
    [Fact]
    public void A_path_that_is_not_ours_is_left_alone()
    {
        LegacyIdentity.OldPathFor("/home/DeskPair/somewhere/else").ShouldBe(string.Empty);
        LegacyIdentity.OldPathFor("/var/lib/something").ShouldBe(string.Empty);
        LegacyIdentity.OldPathFor("/").ShouldBe(string.Empty);
    }

    [Fact]
    public void What_the_old_version_left_behind_is_carried_over()
    {
        string root = Scratch();
        try
        {
            string now = Path.Combine(root, "DeskPair");
            string was = Path.Combine(root, "FastDesk");
            Directory.CreateDirectory(now);
            Write(was, "config.json", "{\"RendezvousServer\":\"rdv.example\"}");
            Write(was, Path.Combine("secrets", "identity-key.bin"), "the key");
            Write(was, Path.Combine("secrets", "password-permanent-h1.bin"), "the hash");

            LegacyIdentity.Migrate(now, NullLogger.Instance);

            File.ReadAllText(Path.Combine(now, "config.json")).ShouldContain("rdv.example");
            File.ReadAllText(Path.Combine(now, "secrets", "identity-key.bin")).ShouldBe("the key");
            File.ReadAllText(Path.Combine(now, "secrets", "password-permanent-h1.bin")).ShouldBe("the hash");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The rule everything in this area keeps arriving at. A machine that already has an identity under
    /// the new name has been that machine to everything that ever connected to it, and replacing it with
    /// an older one is the same loss in the other direction.
    /// </summary>
    [Fact]
    public void Nothing_already_there_is_written_over()
    {
        string root = Scratch();
        try
        {
            string now = Path.Combine(root, "DeskPair");
            string was = Path.Combine(root, "FastDesk");
            Write(now, "config.json", "the current settings");
            Write(now, Path.Combine("secrets", "identity-key.bin"), "the current key");
            Write(was, "config.json", "the old settings");
            Write(was, Path.Combine("secrets", "identity-key.bin"), "the old key");
            Write(was, Path.Combine("secrets", "peer-id.bin"), "the old id");

            LegacyIdentity.Migrate(now, NullLogger.Instance);

            File.ReadAllText(Path.Combine(now, "config.json")).ShouldBe("the current settings");
            File.ReadAllText(Path.Combine(now, "secrets", "identity-key.bin")).ShouldBe("the current key");

            // The one that was genuinely missing is still filled in; this is a merge, not an all-or-nothing.
            File.ReadAllText(Path.Combine(now, "secrets", "peer-id.bin")).ShouldBe("the old id");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The old version's account of itself stays with it; only the identity moves.</summary>
    [Fact]
    public void The_old_logs_are_not_dragged_along()
    {
        string root = Scratch();
        try
        {
            string now = Path.Combine(root, "DeskPair");
            string was = Path.Combine(root, "FastDesk");
            Directory.CreateDirectory(now);
            Write(was, Path.Combine("logs", "desktop.log"), "what the old version did");

            LegacyIdentity.Migrate(now, NullLogger.Instance);

            Directory.Exists(Path.Combine(now, "logs")).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>A machine that never had the old version must not be disturbed, or slowed, by any of this.</summary>
    [Fact]
    public void A_machine_that_never_had_the_old_version_is_untouched()
    {
        string root = Scratch();
        try
        {
            string now = Path.Combine(root, "DeskPair");
            Directory.CreateDirectory(now);

            LegacyIdentity.Migrate(now, NullLogger.Instance);

            Directory.GetFileSystemEntries(now).ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
