namespace DeskPair.Platform.Unix.Terminal;

/// <summary>
/// Which program a Unix shell is, what it is told, and how its end is read. Pure, so each rule is testable
/// on a machine that cannot start one.
///
/// This file is compiled into both Platform.Linux and Platform.MacOS (the pseudo-terminal code is the same
/// on both apart from how a process is started); its types are internal to each.
/// </summary>
internal static class UnixShell
{
    public const string Term = "xterm-256color";

    public const string DefaultPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    /// <summary>
    /// The account's own shell, unless the account is not meant to have one -- the daemon's engine runs as
    /// <c>deskpair</c>, whose shell is nologin -- in which case bash, and sh when there is no bash.
    /// </summary>
    public static string Choose(string? accountShell, Func<string, bool> exists)
    {
        if (!string.IsNullOrWhiteSpace(accountShell)
            && !accountShell.EndsWith("/nologin", StringComparison.Ordinal)
            && !accountShell.EndsWith("/false", StringComparison.Ordinal)
            && exists(accountShell))
        {
            return accountShell;
        }

        return exists("/bin/bash") ? "/bin/bash" : "/bin/sh";
    }

    /// <summary>A login shell, by the convention every shell reads: argv[0] starts with a dash. It then reads the profile that sets PATH.</summary>
    public static string[] Arguments(string shell) => ["-" + Path.GetFileName(shell)];

    /// <summary>
    /// The environment, built from nothing. The engine's own is not passed on: under the daemon it was
    /// assembled for a process that gave root up, and under the app it can hold things -- a token, a
    /// debugging switch -- that are the engine's business and not a shell's.
    /// </summary>
    public static string[] Environment(string user, string home, string shell, string? lang, bool mac)
    {
        string language = !string.IsNullOrEmpty(lang) ? lang : mac ? "en_US.UTF-8" : "C.UTF-8";
        return
        [
            "HOME=" + home,
            "USER=" + user,
            "LOGNAME=" + user,
            "SHELL=" + shell,
            "PATH=" + DefaultPath,
            "TERM=" + Term,
            "COLORTERM=truecolor",
            "LANG=" + language,
        ];
    }

    /// <summary>
    /// A waitpid status as an exit code, the way shells report it: the code itself for a normal exit, 128
    /// plus the signal for a process a signal ended.
    /// </summary>
    public static int ExitCode(int status)
    {
        int signal = status & 0x7F;
        return signal == 0 ? (status >> 8) & 0xFF : 128 + signal;
    }
}
