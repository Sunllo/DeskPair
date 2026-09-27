using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using DeskPair.Core.Update;
using DeskPair.Desktop.Engine;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Services.Update;

/// <summary>How far an install has got, for the one line under the button.</summary>
public sealed record UpdateProgress(UpdateStage Stage, long Done, long Total);

public enum UpdateStage
{
    Downloading,
    Verifying,
    Installing,
}

/// <summary>Something the person can read: the file did not verify, the disk image would not mount, and so on.</summary>
public sealed class UpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads a release file named by a verified manifest, checks it is the file the manifest named, and
/// arranges for it to replace the running program once this process has exited.
///
/// Three things are refused rather than worked around, because each is the difference between an updater
/// and a remote code execution: a file whose hash is not the manifest's; a macOS bundle whose signature
/// or notarisation does not check out; and a manifest that was not signed (that never reaches here -- the
/// button opens the download page instead). The replacement itself is a script that runs after exit; see
/// <see cref="UpdateScripts"/>.
/// </summary>
public sealed class UpdateInstaller(HttpClient http, ILogger log)
{
    /// <summary>Where downloads and staging go; wiped before each attempt.</summary>
    public static string WorkDirectory => Path.Combine(Path.GetTempPath(), "deskpair-update");

    /// <summary>
    /// The whole install. When it returns, the finishing script is running and this process should exit
    /// as soon as it can: the script waits for that.
    /// </summary>
    public async Task InstallAsync(string? portal, ReleaseFile file, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        string work = WorkDirectory;
        try
        {
            if (Directory.Exists(work))
            {
                Directory.Delete(work, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover from a previous attempt that is still locked; the new download gets a new name.
            work = Path.Combine(Path.GetTempPath(), "deskpair-update-" + Environment.ProcessId);
        }

        Directory.CreateDirectory(work);
        string downloaded = Path.Combine(work, file.Name);
        var url = new Uri(UpdateEndpoints.PortalFor(portal) + "/downloads/" + Uri.EscapeDataString(file.Name));
        await DownloadAsync(url, file.Sha256, file.Size, downloaded, progress, ct).ConfigureAwait(false);

        progress?.Report(new UpdateProgress(UpdateStage.Installing, 0, 0));
        string script;
        if (OperatingSystem.IsWindows())
        {
            script = StageWindows(downloaded, work);
        }
        else if (OperatingSystem.IsMacOS())
        {
            script = StageMacOS(downloaded, work);
        }
        else if (OperatingSystem.IsLinux())
        {
            script = await StageLinuxAsync(downloaded, work, ct).ConfigureAwait(false);
        }
        else
        {
            throw new UpdateException("This system has no installer.");
        }

        StartFinisher(script);
        log.LogInformation("Update {File} verified and staged; the finishing script is running and this process is exiting", file.Name);
    }

    /// <summary>
    /// Streams the file to disk while hashing it, and refuses it if the hash is not the manifest's. The
    /// file is written under a temporary name and renamed only once it has verified, so nothing else can
    /// ever pick up a half-downloaded or wrong file by its final name.
    /// </summary>
    public async Task DownloadAsync(Uri url, string expectedSha256, long expectedSize, string destination, IProgress<UpdateProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        string partial = destination + ".part";
        using HttpResponseMessage response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new UpdateException($"{url} answered {(int)response.StatusCode}.");
        }

        long total = response.Content.Headers.ContentLength ?? expectedSize;
        using var sha = SHA256.Create();
        await using (Stream body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            byte[] buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.TransformBlock(buffer, 0, read, null, 0);
                done += read;
                progress?.Report(new UpdateProgress(UpdateStage.Downloading, done, total));
            }

            sha.TransformFinalBlock([], 0, 0);
        }

        progress?.Report(new UpdateProgress(UpdateStage.Verifying, 0, 0));
        string actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new UpdateException($"The downloaded file is not the one the release names (SHA-256 {actual[..12]}… instead of {expectedSha256[..12]}…). Nothing was installed.");
        }

        File.Move(partial, destination, overwrite: true);
    }

    /// <summary>
    /// The folder the program's files are in, inside an extracted archive. The archives carry one
    /// top-level folder named after the release; older ones might not.
    /// </summary>
    public static string LocateStagedRoot(string extracted, string executableName)
    {
        if (File.Exists(Path.Combine(extracted, executableName)))
        {
            return extracted;
        }

        foreach (string dir in Directory.GetDirectories(extracted))
        {
            if (File.Exists(Path.Combine(dir, executableName)))
            {
                return dir;
            }
        }

        throw new UpdateException($"The archive does not contain {executableName}.");
    }

    private static string StageWindows(string zip, string work)
    {
        string extracted = Path.Combine(work, "extracted");
        ZipFile.ExtractToDirectory(zip, extracted);
        string staged = LocateStagedRoot(extracted, "DeskPair.exe");
        string executable = Environment.ProcessPath ?? throw new UpdateException("Cannot work out where this program is.");
        string installDir = Path.GetDirectoryName(executable) ?? throw new UpdateException("Cannot work out where this program is.");
        string script = Path.Combine(work, "update.cmd");
        File.WriteAllText(script, UpdateScripts.Windows(Environment.ProcessId, staged, installDir, executable));
        return script;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static string StageMacOS(string dmg, string work)
    {
        string bundle = BundleOf(Environment.ProcessPath ?? string.Empty)
            ?? throw new UpdateException("DeskPair is not running from an app bundle, so there is nothing to replace. Install the new version from the disk image.");
        string mount = Path.Combine(work, "mnt");
        Directory.CreateDirectory(mount);
        Run("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-mountpoint", mount, dmg], "The disk image could not be opened");
        string? app = Directory.GetDirectories(mount, "*.app").FirstOrDefault();
        if (app is null)
        {
            Run("/usr/bin/hdiutil", ["detach", mount, "-quiet"], null);
            throw new UpdateException("The disk image holds no application.");
        }

        // The signature is what makes the copy the same program to macOS -- and to TCC, which keyed the
        // screen-recording grant on it. A bundle that fails either check is not installed, whatever the
        // hash said: the hash proves the bytes are the portal's, the signature proves they are ours.
        try
        {
            Run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app], "The new version's code signature does not verify");
            Run("/usr/sbin/spctl", ["--assess", "--type", "execute", app], "The new version is not accepted by Gatekeeper");
        }
        catch
        {
            Run("/usr/bin/hdiutil", ["detach", mount, "-quiet"], null);
            throw;
        }

        string script = Path.Combine(work, "update.sh");
        File.WriteAllText(script, UpdateScripts.MacOS(Environment.ProcessId, app, bundle, mount));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task<string> StageLinuxAsync(string tarball, string work, CancellationToken ct)
    {
        string extracted = Path.Combine(work, "extracted");
        Directory.CreateDirectory(extracted);
        Run("/usr/bin/tar", ["-xzf", tarball, "-C", extracted], "The archive could not be extracted");
        string staged = LocateStagedRoot(extracted, "DeskPair");
        string newExecutable = Path.Combine(staged, "DeskPair");
        File.SetUnixFileMode(newExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        // The daemon, when there is one, is updated first and by the same road the settings page uses: the
        // root half of --install-service copies the new program to /opt and restarts the unit. The person
        // gets the polkit prompt; a "no" leaves the daemon on the old version and the app on the new one,
        // which is said rather than hidden.
        if (UnattendedInstall.IsInstalled())
        {
            await Task.Run(() =>
            {
                string? pkexec = new[] { "/usr/bin/pkexec", "/bin/pkexec" }.FirstOrDefault(File.Exists);
                if (pkexec is null)
                {
                    throw new UpdateException($"The background service needs updating too, and there is no pkexec to ask with. Run: sudo {UpdateScripts.Quote(newExecutable)} --install-service --system-stage --from {UpdateScripts.Quote(ServerRole.DefaultDataDir())}");
                }

                Run(pkexec, [newExecutable, "--install-service", "--system-stage", "--from", ServerRole.DefaultDataDir()], "The background service was not updated");
            }, ct).ConfigureAwait(false);
        }

        string executable = Environment.ProcessPath ?? throw new UpdateException("Cannot work out where this program is.");
        string script = Path.Combine(work, "update.sh");
        File.WriteAllText(script, UpdateScripts.Linux(Environment.ProcessId, newExecutable, executable));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    /// <summary>The .app a Contents/MacOS/DeskPair path is inside, or null when it is not inside one.</summary>
    public static string? BundleOf(string executable)
    {
        DirectoryInfo? macos = executable.Length == 0 ? null : new FileInfo(executable).Directory;
        DirectoryInfo? contents = macos?.Parent;
        DirectoryInfo? bundle = contents?.Parent;
        return macos?.Name == "MacOS" && contents?.Name == "Contents" && bundle is not null
            && bundle.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            ? bundle.FullName
            : null;
    }

    private static void StartFinisher(string script)
    {
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", script }, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { script }, UseShellExecute = false };
        if (Process.Start(start) is null)
        {
            throw new UpdateException("The finishing step could not be started.");
        }
    }

    /// <summary>Runs a tool and turns a non-zero exit into a sentence; <paramref name="failure"/> null means the exit code is not checked.</summary>
    private static void Run(string file, string[] arguments, string? failure)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new UpdateException($"{file} could not be started.");
        string error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (failure is not null && process.ExitCode != 0)
        {
            throw new UpdateException($"{failure} ({Path.GetFileName(file)} exited {process.ExitCode}: {error.Trim()}).");
        }
    }
}
