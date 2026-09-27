using System.Collections.Concurrent;

namespace DeskPair.Core.FileTransfer;

public enum FsEntryKind
{
    Directory,
    File,
    Symlink,
    Drive,
}

public sealed record FsEntry(string Name, FsEntryKind Kind, long Size, DateTimeOffset Modified, bool Hidden);

public sealed record FsStat(long Size, DateTimeOffset Modified, bool IsDirectory);

/// <summary>Minimal file system surface used by the transfer engine; swapped for an in-memory one in tests.</summary>
public interface IFileSystem
{
    char Separator { get; }

    string Combine(string a, string b);

    string GetFileName(string path);

    string GetDirectoryName(string path);

    IReadOnlyList<FsEntry> ListDirectory(string path, bool includeHidden);

    /// <summary>Recursively lists the files under <paramref name="root"/> as relative paths (using <see cref="Separator"/>); a single file yields itself.</summary>
    IReadOnlyList<(string RelativePath, long Size, DateTimeOffset Modified)> EnumerateFiles(string root, bool includeHidden);

    FsStat? Stat(string path);

    Stream OpenRead(string path);

    /// <summary>Opens for writing, creating parent directories; <paramref name="append"/> resumes an existing file.</summary>
    Stream OpenWrite(string path, bool append);

    void CreateDirectory(string path);

    void DeleteFile(string path);

    void DeleteDirectory(string path, bool recursive);

    void Move(string from, string to);

    void SetModified(string path, DateTimeOffset modified);
}

public sealed class LocalFileSystem : IFileSystem
{
    public static LocalFileSystem Instance { get; } = new();

    public char Separator => Path.DirectorySeparatorChar;

    public string Combine(string a, string b) => Path.Combine(a, b);

    public string GetFileName(string path) => Path.GetFileName(path);

    public string GetDirectoryName(string path) => Path.GetDirectoryName(path) ?? string.Empty;

    public IReadOnlyList<FsEntry> ListDirectory(string path, bool includeHidden)
    {
        if (path.Length == 0 && OperatingSystem.IsWindows())
        {
            return DriveInfo.GetDrives().Where(d => d.IsReady)
                .Select(d => new FsEntry(d.Name, FsEntryKind.Drive, 0, DateTimeOffset.MinValue, false)).ToList();
        }

        var dir = new DirectoryInfo(path.Length == 0 ? "/" : path);
        var list = new List<FsEntry>();
        foreach (FileSystemInfo info in dir.EnumerateFileSystemInfos())
        {
            bool hidden = info.Attributes.HasFlag(FileAttributes.Hidden) || info.Name.StartsWith('.');
            if (hidden && !includeHidden)
            {
                continue;
            }

            bool link = info.LinkTarget is not null;
            if (info is DirectoryInfo)
            {
                list.Add(new FsEntry(info.Name, link ? FsEntryKind.Symlink : FsEntryKind.Directory, 0, info.LastWriteTimeUtc, hidden));
            }
            else if (info is FileInfo f)
            {
                list.Add(new FsEntry(info.Name, link ? FsEntryKind.Symlink : FsEntryKind.File, f.Length, f.LastWriteTimeUtc, hidden));
            }
        }

        return list;
    }

    public IReadOnlyList<(string RelativePath, long Size, DateTimeOffset Modified)> EnumerateFiles(string root, bool includeHidden)
    {
        var result = new List<(string, long, DateTimeOffset)>();
        if (File.Exists(root))
        {
            var f = new FileInfo(root);
            result.Add((f.Name, f.Length, f.LastWriteTimeUtc));
            return result;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = includeHidden ? FileAttributes.System : FileAttributes.Hidden | FileAttributes.System, IgnoreInaccessible = true };
        foreach (string file in Directory.EnumerateFiles(root, "*", options))
        {
            var f = new FileInfo(file);
            result.Add((Path.GetRelativePath(root, file), f.Length, f.LastWriteTimeUtc));
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return result;
    }

    public FsStat? Stat(string path)
    {
        if (File.Exists(path))
        {
            var f = new FileInfo(path);
            return new FsStat(f.Length, f.LastWriteTimeUtc, false);
        }

        return Directory.Exists(path) ? new FsStat(0, Directory.GetLastWriteTimeUtc(path), true) : null;
    }

    // ReadWrite, not Read: a file another process is writing (a log, a document still open) could not be
    // fetched at all, because a reader that allows others only to read is refused when a writer is already
    // there. Reading alongside a writer is what a person fetching a live log wants.
    public Stream OpenRead(string path) => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan | FileOptions.Asynchronous);

    public Stream OpenWrite(string path, bool append)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!append)
        {
            return new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        }

        // "Append" here means "resume an existing partial": the caller trims it back to a whole-block
        // boundary (SetLength) and then writes from there. FileMode.Append forbids both truncating and
        // seeking, so a resume against a real file threw "Unable to truncate ... a file opened in Append
        // mode". Open read/write and position at the end instead, which supports both.
        var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        stream.Seek(0, SeekOrigin.End);
        return stream;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    public void Move(string from, string to) => File.Move(from, to, overwrite: true);

    public void SetModified(string path, DateTimeOffset modified) => File.SetLastWriteTimeUtc(path, modified.UtcDateTime);
}

/// <summary>Path-keyed in-memory file system with '/' separators, for tests.</summary>
public sealed class InMemoryFileSystem : IFileSystem
{
    private readonly ConcurrentDictionary<string, MemoryFile> _files = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _dirs = new(StringComparer.Ordinal);

    private sealed class MemoryFile
    {
        public byte[] Data = [];
        public DateTimeOffset Modified = DateTimeOffset.UnixEpoch;
    }

    public char Separator => '/';

    public IEnumerable<string> Files => _files.Keys;

    public byte[] Read(string path) => _files.TryGetValue(Norm(path), out MemoryFile? f) ? f.Data : throw new FileNotFoundException(path);

    public void Write(string path, byte[] data, DateTimeOffset? modified = null)
    {
        path = Norm(path);
        _files[path] = new MemoryFile { Data = data, Modified = modified ?? DateTimeOffset.UnixEpoch };
        EnsureParents(path);
    }

    public string Combine(string a, string b) => a.Length == 0 ? b : a.TrimEnd('/') + "/" + b.TrimStart('/');

    public string GetFileName(string path) => path.TrimEnd('/').Split('/').Last();

    public string GetDirectoryName(string path)
    {
        int i = path.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? string.Empty : path[..i];
    }

    public IReadOnlyList<FsEntry> ListDirectory(string path, bool includeHidden)
    {
        string prefix = path.Length == 0 ? string.Empty : Norm(path).TrimEnd('/') + "/";
        var entries = new Dictionary<string, FsEntry>(StringComparer.Ordinal);
        foreach ((string p, MemoryFile f) in _files)
        {
            if (!p.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string rest = p[prefix.Length..];
            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                entries[rest] = new FsEntry(rest, FsEntryKind.File, f.Data.Length, f.Modified, rest.StartsWith('.'));
            }
            else
            {
                string d = rest[..slash];
                entries.TryAdd(d, new FsEntry(d, FsEntryKind.Directory, 0, DateTimeOffset.UnixEpoch, d.StartsWith('.')));
            }
        }

        foreach (string d in _dirs.Keys)
        {
            if (d.StartsWith(prefix, StringComparison.Ordinal))
            {
                string rest = d[prefix.Length..].TrimEnd('/');
                if (rest.Length > 0 && !rest.Contains('/'))
                {
                    entries.TryAdd(rest, new FsEntry(rest, FsEntryKind.Directory, 0, DateTimeOffset.UnixEpoch, rest.StartsWith('.')));
                }
            }
        }

        return entries.Values.Where(e => includeHidden || !e.Hidden).OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<(string RelativePath, long Size, DateTimeOffset Modified)> EnumerateFiles(string root, bool includeHidden)
    {
        root = Norm(root);
        if (_files.TryGetValue(root, out MemoryFile? single))
        {
            return [(GetFileName(root), single.Data.Length, single.Modified)];
        }

        string prefix = root.TrimEnd('/') + "/";
        return _files.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => (kv.Key[prefix.Length..], (long)kv.Value.Data.Length, kv.Value.Modified))
            .Where(t => includeHidden || !t.Item1.Split('/').Any(s => s.StartsWith('.')))
            .OrderBy(t => t.Item1, StringComparer.Ordinal)
            .ToList();
    }

    public FsStat? Stat(string path)
    {
        path = Norm(path);
        if (_files.TryGetValue(path, out MemoryFile? f))
        {
            return new FsStat(f.Data.Length, f.Modified, false);
        }

        string prefix = path.TrimEnd('/') + "/";
        return _dirs.ContainsKey(prefix) || _files.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal)) ? new FsStat(0, DateTimeOffset.UnixEpoch, true) : null;
    }

    public Stream OpenRead(string path) => new MemoryStream(Read(path), writable: false);

    public Stream OpenWrite(string path, bool append)
    {
        path = Norm(path);
        EnsureParents(path);
        MemoryFile file = _files.GetOrAdd(path, _ => new MemoryFile());
        if (!append)
        {
            file.Data = [];
        }

        return new WriteBackStream(file, append);
    }

    public void CreateDirectory(string path) => _dirs[Norm(path).TrimEnd('/') + "/"] = 0;

    public void DeleteFile(string path)
    {
        if (!_files.TryRemove(Norm(path), out _))
        {
            throw new FileNotFoundException(path);
        }
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        string prefix = Norm(path).TrimEnd('/') + "/";
        bool any = _files.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
        if (any && !recursive)
        {
            throw new IOException("Directory not empty.");
        }

        foreach (string k in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            _files.TryRemove(k, out _);
        }

        _dirs.TryRemove(prefix, out _);
    }

    public void Move(string from, string to)
    {
        if (!_files.TryRemove(Norm(from), out MemoryFile? f))
        {
            throw new FileNotFoundException(from);
        }

        _files[Norm(to)] = f;
    }

    public void SetModified(string path, DateTimeOffset modified)
    {
        if (_files.TryGetValue(Norm(path), out MemoryFile? f))
        {
            f.Modified = modified;
        }
    }

    private void EnsureParents(string path)
    {
        int i = path.LastIndexOf('/');
        if (i > 0)
        {
            _dirs[path[..i] + "/"] = 0;
        }
    }

    private static string Norm(string path) => path.Replace('\\', '/').TrimStart('/');

    private sealed class WriteBackStream(MemoryFile file) : MemoryStream
    {
        private bool _flushed;

        public WriteBackStream(MemoryFile f, bool append) : this(f)
        {
            if (append)
            {
                Write(f.Data, 0, f.Data.Length);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_flushed)
            {
                _flushed = true;
                file.Data = ToArray();
            }

            base.Dispose(disposing);
        }
    }
}
