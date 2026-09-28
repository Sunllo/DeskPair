using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace DeskPair.Desktop.Engine;

/// <summary>
/// The Windows Firewall rules for the host engine. Windows asks the user to allow a program the first time it
/// listens, once per network profile; creating the rules in advance (which needs administrator rights once)
/// stops the prompt. Without them only direct and local-network connections are blocked - connections made
/// through the rendezvous and relay servers are outbound and keep working.
/// </summary>
public static class FirewallRules
{
    public const string RuleName = "Sunllo DeskPair";

    /// <summary>Adds allow rules for this executable on private and domain networks (never public).</summary>
    public static int Add(ILoggerFactory logs)
    {
        ILogger log = logs.CreateLogger("firewall");
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Firewall rules are a Windows feature.");
            return 2;
        }

        string exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length == 0)
        {
            Console.Error.WriteLine("Could not determine the program path.");
            return 2;
        }

        Remove(logs, quiet: true);
        foreach (string protocol in (string[])["TCP", "UDP"])
        {
            int code = Netsh(log, $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{exe}\" protocol={protocol} profile=private,domain enable=yes");
            if (code != 0)
            {
                Console.Error.WriteLine($"Could not add the {protocol} rule (exit code {code}). Run this as an administrator.");
                return 2;
            }
        }

        Console.WriteLine($"Firewall rules for \"{RuleName}\" added (private and domain networks).");
        return 0;
    }

    public static int Remove(ILoggerFactory logs, bool quiet = false)
    {
        ILogger log = logs.CreateLogger("firewall");
        if (!OperatingSystem.IsWindows())
        {
            return 2;
        }

        int code = Netsh(log, $"advfirewall firewall delete rule name=\"{RuleName}\"");
        if (!quiet)
        {
            Console.WriteLine(code == 0 ? $"Firewall rules for \"{RuleName}\" removed." : "No rules to remove.");
        }

        return 0;
    }

    /// <summary>
    /// Removes every rule that names <paramref name="program"/>, whoever made it: chiefly the pair Windows makes itself
    /// when somebody answers its "allow access" prompt -- inbound, allowed, on private and public networks alike --
    /// which it names after the program and which nothing else ever takes away. By path, so the rules of a copy of
    /// DeskPair elsewhere on the disk stay where they are.
    ///
    /// Through PowerShell's firewall module rather than netsh. Windows keeps the path as its prompt wrote it, in lower
    /// case, and the module's program filter matches it whatever the case -- seen on a machine with such a pair --
    /// where netsh's is not documented either way.
    /// </summary>
    public static int RemoveAllFor(string program, ILogger log)
    {
        if (!OperatingSystem.IsWindows() || program.Length == 0)
        {
            return 2;
        }

        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        (int code, string output) = Run(log, powershell, ["-NoProfile", "-NonInteractive", "-Command", RemoveAllForScript(program)]);
        if (code == 0)
        {
            log.LogInformation("Firewall rules naming {Program} removed: {Count}", program, output.Trim());
        }

        return code;
    }

    /// <summary>What <see cref="RemoveAllFor"/> runs: prints how many rules it found, and fails if one would not go.</summary>
    internal static string RemoveAllForScript(string program) =>
        $"$r = @(Get-NetFirewallApplicationFilter -Program '{program.Replace("'", "''", StringComparison.Ordinal)}' "
        + "-ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue); "
        + "if ($r.Count) { $r | Remove-NetFirewallRule -ErrorAction Stop }; $r.Count";

    private static int Netsh(ILogger log, string arguments)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("netsh", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return -1;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(20_000);
            log.LogDebug("netsh {Arguments}: {Output}", arguments, output.Trim());
            return process.ExitCode;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "netsh failed");
            return -1;
        }
    }

    private static (int Code, string Output) Run(ILogger log, string file, IEnumerable<string> arguments)
    {
        try
        {
            var start = new ProcessStartInfo(file)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(start);
            if (process is null)
            {
                return (-1, string.Empty);
            }

            Task<string> errors = process.StandardError.ReadToEndAsync();
            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                process.Kill();
                return (-1, output);
            }

            if (process.ExitCode != 0)
            {
                log.LogWarning("{File} exited {Code}: {Errors}", Path.GetFileName(file), process.ExitCode, errors.Result.Trim());
            }

            return (process.ExitCode, output);
        }
        catch (Exception e)
        {
            log.LogWarning(e, "{File} failed", Path.GetFileName(file));
            return (-1, string.Empty);
        }
    }
}
