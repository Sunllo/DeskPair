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
}
