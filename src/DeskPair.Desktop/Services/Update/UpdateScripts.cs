namespace DeskPair.Desktop.Services.Update;

/// <summary>
/// The scripts that finish an update after this process has exited.
///
/// A program cannot replace its own executable while it runs (Windows refuses outright; on the other two
/// it works but the running process and the file on disk stop being the same program), so the last step
/// is handed to the shell: wait for our pid to go, copy the new files over the old, start the new one, and
/// remove yourself. Text rather than code because a script can be read in a test, and every line here has
/// a reason a test can name.
/// </summary>
public static class UpdateScripts
{
    /// <summary>
    /// Windows: <c>update.cmd</c>. Waits for the process, copies the staged folder over the install folder
    /// with robocopy (which retries while the antivirus is still reading the old binary), starts the new
    /// executable, and deletes the staging folder and itself.
    /// </summary>
    public static string Windows(int pid, string stagedDir, string installDir, string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        return $"""
            @echo off
            :wait
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul
            if not errorlevel 1 (
              timeout /t 1 /nobreak >nul
              goto wait
            )
            robocopy "{stagedDir}" "{installDir}" /E /R:20 /W:1 /NFL /NDL /NJH /NJS >nul
            if errorlevel 8 (
              echo The update could not be copied into "{installDir}".
              pause
              exit /b 1
            )
            start "" "{executable}"
            rmdir /s /q "{stagedDir}"
            del "%~f0"

            """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// macOS: replaces the bundle from a mounted, verified disk image, detaches it, opens the new bundle.
    /// <c>ditto</c> rather than <c>cp</c>: it keeps the resource forks, the extended attributes and the
    /// code signature's staple, which is what makes the copy still the notarised app.
    /// </summary>
    public static string MacOS(int pid, string mountedApp, string bundle, string mountPoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mountedApp);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle);
        ArgumentException.ThrowIfNullOrWhiteSpace(mountPoint);
        return $"""
            #!/bin/sh
            while kill -0 {pid} 2>/dev/null; do sleep 1; done
            rm -rf {Quote(bundle)}
            ditto {Quote(mountedApp)} {Quote(bundle)}
            hdiutil detach {Quote(mountPoint)} -quiet
            open -n {Quote(bundle)}
            rm -f "$0"

            """.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// Linux: the app is one file. Moves the new one over the old (a rename, so nothing is ever half a
    /// program), makes it executable, and starts it.
    /// </summary>
    public static string Linux(int pid, string newExecutable, string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        return $"""
            #!/bin/sh
            while kill -0 {pid} 2>/dev/null; do sleep 1; done
            chmod +x {Quote(newExecutable)}
            mv -f {Quote(newExecutable)} {Quote(executable)}
            {Quote(executable)} >/dev/null 2>&1 &
            rm -f "$0"

            """.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Single quotes, with any single quote in the value closed, escaped and reopened.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
