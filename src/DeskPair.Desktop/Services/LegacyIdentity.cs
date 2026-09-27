using Microsoft.Extensions.Logging;
using DeskPair.Core.Config;
using DeskPair.Desktop.Engine;
using DeskPair.Platform.Abstractions.Security;

namespace DeskPair.Desktop.Services;

/// <summary>
/// Carries a machine's identity across the rename from FastDesk to DeskPair.
///
/// The rename moved two things nobody would think of as code. The data directory, from
/// <c>Sunllo/FastDesk</c> to <c>Sunllo/DeskPair</c>; and on a Mac the keychain service, from
/// <c>Sunllo FastDesk</c> to <c>Sunllo DeskPair</c>. Neither was migrated, and neither fails when it is
/// missing: an upgraded machine finds an empty store, and an empty store is what tells the host to mint an
/// identity key and save it.
///
/// So the machine comes up working, with a new key, a new id at the rendezvous server and a new permanent
/// password -- and every device that had trusted it quietly stops recognising it. Nothing is logged as an
/// error because nothing went wrong, in the sense the code had of "wrong". Found on a real Mac that had
/// been reachable as one id for days and was about to become another.
///
/// The rule is the one this whole area keeps arriving at: fill in what is missing, never write over what
/// is there, and treat "cannot read" as a reason to stop rather than a reason to replace.
/// </summary>
internal static class LegacyIdentity
{
    private const string OldName = "FastDesk";
    private const string NewName = "DeskPair";

    /// <summary>The keychain service the macOS build used before the rename.</summary>
    private const string OldKeychainService = "Sunllo FastDesk";

    /// <summary>What belongs to the machine rather than to an installation of it.</summary>
    private static readonly string[] Keys =
    [
        "identity-key",
        "peer-id",
        "machine-id",
        "password-salt",
        "password-permanent-h1",
    ];

    /// <summary>
    /// Moves anything the old name left behind into <paramref name="dataDir"/>, if it is not there already.
    ///
    /// Safe to call on every start and on a machine that never had the old version: the work is skipped
    /// when the new store already has an identity, and skipped again when the old one has none.
    /// </summary>
    public static void Migrate(string dataDir, ILogger log)
    {
        try
        {
            MigrateFiles(dataDir, log);
            MigrateKeychain(dataDir, log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never fatal. A machine that cannot be migrated still runs; it just runs as a new machine,
            // which is what this is trying to avoid and not something worth refusing to start over.
            log.LogWarning(e, "Could not carry the previous version's identity over");
        }
    }

    /// <summary>
    /// The data directory, which is every platform: secrets on Windows and Linux, and the settings
    /// everywhere. The old path is this one with the product's old name in it, which is exactly how it
    /// was produced.
    /// </summary>
    private static void MigrateFiles(string dataDir, ILogger log)
    {
        string old = OldPathFor(dataDir);
        if (old.Length == 0 || !Directory.Exists(old) || string.Equals(old, dataDir, StringComparison.Ordinal))
        {
            return;
        }

        // Not the logs. They are the old version's account of itself and belong with it.
        CopyIfMissing(Path.Combine(old, "config.json"), Path.Combine(dataDir, "config.json"), log);

        string oldSecrets = Path.Combine(old, "secrets");
        if (!Directory.Exists(oldSecrets))
        {
            return;
        }

        string newSecrets = Path.Combine(dataDir, "secrets");
        Directory.CreateDirectory(newSecrets);
        foreach (string key in Keys)
        {
            CopyIfMissing(Path.Combine(oldSecrets, key + ".bin"), Path.Combine(newSecrets, key + ".bin"), log);
        }
    }

    /// <summary>
    /// The keychain, which is macOS only and the half that matters there: the file store under the old
    /// data directory is empty on a Mac, because the engine kept everything in the login keychain.
    /// </summary>
    private static void MigrateKeychain(string dataDir, ILogger log)
    {
#if WINDOWS
        // The macOS platform assembly is not referenced by the Windows target at all, so this half does
        // not exist there -- and neither does the keychain it is about.
        _ = (dataDir, log);
#else
        if (!OperatingSystem.IsMacOS() || !Platform.MacOS.Security.MacSecretStore.IsAvailable)
        {
            return;
        }

        ISecretStore now = PlatformServices.SecretStoreFor(dataDir);
        foreach (string key in Keys)
        {
            try
            {
                if (now.GetAsync(key).AsTask().GetAwaiter().GetResult() is not null)
                {
                    continue; // this machine already has one under this name, and it is the real one
                }

                if (Platform.MacOS.Security.MacSecretStore.ReadLegacy(OldKeychainService, key) is { } value)
                {
                    now.SetAsync(key, value).AsTask().GetAwaiter().GetResult();
                    log.LogInformation("Carried {Key} over from the previous version", key);
                }
            }
            catch (SecretUnreadableException e)
            {
                // Present and refused -- a declined keychain prompt, or an item whose access list no
                // longer names this binary. Reported and skipped, because the alternative is writing a
                // replacement, and a replacement is how the identity is lost rather than kept.
                log.LogWarning("{Key} could not be carried over ({Reason}); it has been left alone", key, e.Message);
            }
        }
#endif
    }

    /// <summary>
    /// The same directory under the product's old name, or empty when this path does not look like one
    /// this program chose. Only the last segment is rewritten: a user whose home directory happens to be
    /// called DeskPair should not have their path rewritten out from under them.
    /// </summary>
    internal static string OldPathFor(string dataDir)
    {
        string trimmed = dataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string leaf = Path.GetFileName(trimmed);
        string? parent = Path.GetDirectoryName(trimmed);
        if (parent is null || leaf.Length == 0)
        {
            return string.Empty;
        }

        // Windows and macOS use the product name as written; Linux uses it lower-cased.
        string? was = leaf switch
        {
            NewName => OldName,
            "deskpair" => "fastdesk",
            _ => null,
        };

        return was is null ? string.Empty : Path.Combine(parent, was);
    }

    private static void CopyIfMissing(string from, string to, ILogger log)
    {
        try
        {
            if (!File.Exists(from) || File.Exists(to))
            {
                return;
            }

            File.Copy(from, to);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(to, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            log.LogInformation("Carried {File} over from the previous version", Path.GetFileName(from));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning(e, "Could not carry {File} over", Path.GetFileName(from));
        }
    }
}
