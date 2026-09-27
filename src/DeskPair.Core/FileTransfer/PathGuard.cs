namespace DeskPair.Core.FileTransfer;

/// <summary>Rejects relative file names a peer could use to escape the target directory.</summary>
public static class PathGuard
{
    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Validates a peer-supplied relative path (either separator) and returns its segments.</summary>
    public static string[] ValidateRelative(string relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            throw new PathGuardException("Empty file name.");
        }

        if (relative.Length > 4096 || relative.Any(c => c < 0x20 || c == '\0'))
        {
            throw new PathGuardException("Invalid characters in file name.");
        }

        if (relative.StartsWith('/') || relative.StartsWith('\\') || (relative.Length >= 2 && relative[1] == ':'))
        {
            throw new PathGuardException("Absolute paths are not allowed.");
        }

        string[] segments = relative.Split(['/', '\\'], StringSplitOptions.None);
        foreach (string s in segments)
        {
            if (s.Length == 0 || s == "." || s == "..")
            {
                throw new PathGuardException("Path traversal is not allowed.");
            }

            if (s.IndexOfAny(['<', '>', ':', '"', '|', '?', '*']) >= 0)
            {
                throw new PathGuardException("Invalid characters in file name.");
            }

            string stem = s.Split('.')[0];
            if (WindowsReserved.Contains(stem))
            {
                throw new PathGuardException("Reserved file name.");
            }

            if (s.EndsWith(' ') || s.EndsWith('.'))
            {
                throw new PathGuardException("File names may not end with a space or period.");
            }
        }

        return segments;
    }

    /// <summary>Joins a validated relative path under <paramref name="baseDir"/> using the file system's separator.</summary>
    public static string Join(IFileSystem fs, string baseDir, string relative)
    {
        string result = baseDir;
        foreach (string segment in ValidateRelative(relative))
        {
            result = fs.Combine(result, segment);
        }

        return result;
    }
}

public sealed class PathGuardException(string message) : Exception(message);
