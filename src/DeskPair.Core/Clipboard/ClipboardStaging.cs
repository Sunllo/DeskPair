using Microsoft.Extensions.Logging;

namespace DeskPair.Core.Clipboard;

/// <summary>
/// Where bytes fetched for a paste land before the pasting application takes them.
///
/// The directory for a promise is created on the first fetch and never at copy time, so a promise nobody
/// pastes costs nothing at all. Cleanup has two halves because neither alone is enough: a promise that is
/// superseded is swept as soon as nothing is reading it, and a startup sweep removes what a crash mid-paste
/// left behind, which is the only thing that can.
/// </summary>
public sealed class ClipboardStaging
{
    /// <summary>Old enough that no live session could still own it.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    private readonly string _root;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public ClipboardStaging(ILogger log, string? root = null, TimeProvider? time = null)
    {
        _log = log;
        _time = time ?? TimeProvider.System;
        _root = root ?? DefaultRoot();
    }

    public string Root => _root;

    /// <summary>
    /// The per-platform cache location. Linux deliberately uses XDG_CACHE_HOME and not XDG_RUNTIME_DIR,
    /// which is RAM and would hold a pasted file in memory.
    /// </summary>
    public static string DefaultRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            string cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x
                ? x
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
            return Path.Combine(cache, "DeskPair", "clipboard");
        }

        return Path.Combine(Path.GetTempPath(), "DeskPair", "clipboard");
    }

    /// <summary>The directory for one promise, created on demand.</summary>
    public string DirectoryFor(string token)
    {
        string dir = Path.Combine(_root, Sanitise(token));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Where a promised file will be written. Parent directories are created; the file is not.</summary>
    public string PathFor(string token, string relativePath)
    {
        string full = Path.GetFullPath(Path.Combine(DirectoryFor(token), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string dir = Path.Combine(_root, Sanitise(token));

        // The relative path came from the peer. It has been validated once already; this is the second line,
        // and it is cheap.
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Staged path escapes its promise directory: {relativePath}");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        return full;
    }

    public void Discard(string token)
    {
        string dir = Path.Combine(_root, Sanitise(token));
        TryDelete(dir);
    }

    /// <summary>Removes promise directories older than <see cref="StaleAfter"/>. The answer to crashing mid-paste.</summary>
    public int SweepStale()
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        int removed = 0;
        DateTimeOffset cutoff = _time.GetUtcNow() - StaleAfter;
        foreach (string dir in Directory.EnumerateDirectories(_root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < cutoff && TryDelete(dir))
                {
                    removed++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Someone else's, or in use. Leave it; the next sweep will try again.
            }
        }

        if (removed > 0)
        {
            _log.LogInformation("Removed {Count} clipboard staging directories left behind by an earlier run", removed);
        }

        return removed;
    }

    private bool TryDelete(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return false;
            }

            Directory.Delete(dir, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogDebug(e, "Could not remove clipboard staging directory {Directory} yet", dir);
            return false;
        }
    }

    /// <summary>A token reaches us over the wire, so it never becomes a path component unexamined.</summary>
    private static string Sanitise(string token)
    {
        Span<char> buffer = stackalloc char[Math.Min(token.Length, 64)];
        for (int i = 0; i < buffer.Length; i++)
        {
            char c = token[i];
            buffer[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_';
        }

        return buffer.Length == 0 ? "unnamed" : new string(buffer);
    }
}
