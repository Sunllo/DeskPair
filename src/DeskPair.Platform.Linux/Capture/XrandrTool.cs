using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DeskPair.Platform.Linux.Capture;

/// <summary>Runs the <c>xrandr</c> tool, for the changes that are safer made by it than by hand (see <see cref="X11DisplayModes"/>).</summary>
internal static class XrandrTool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Whether <paramref name="name"/> can go on xrandr's command line as an output or a mode: it came from XRandR,
    /// but nothing that reads as an option, and nothing with a space in it, is let through.
    /// </summary>
    public static bool IsSafeName(string name) => name.Length > 0 && !name.StartsWith('-') && !name.Any(char.IsWhiteSpace);

    /// <summary>Runs <c>xrandr</c> with <paramref name="arguments"/>; null when it worked, otherwise why not, in words fit for a viewer.</summary>
    public static string? Run(IReadOnlyList<string> arguments, ILogger log)
    {
        var start = new ProcessStartInfo("xrandr")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("xrandr did not start");
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return "xrandr did not finish.";
            }

            _ = stdout.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                string error = stderr.GetAwaiter().GetResult().Trim();
                return error.Length > 0 ? error : $"xrandr exited with code {process.ExitCode}.";
            }

            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.LogWarning(e, "xrandr could not be run");
            return "The xrandr tool is not installed on this computer.";
        }
    }
}
