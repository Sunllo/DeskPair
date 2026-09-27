using System.Text;
using DeskPair.Core.Config;
using DeskPair.Core.Transport;
using DeskPair.Desktop.Services;
using DeskPair.Protocol.Ipc;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeskPair.Tools.Screenshots;

/// <summary>
/// Everything the pictures show. The machines and people are made up; the IDs are rotations of 123456789, the
/// placeholder the rest of the repository uses, so none of them can be mistaken for a real desk; addresses come
/// from the ranges set aside for documentation (203.0.113.0/24, 198.51.100.0/24).
/// </summary>
internal static class Samples
{
    public const string ThisDesk = "123456789";
    public const string Reception = "234567891";
    public const string BuildServer = "345678912";
    public const string AlicesMac = "456789123";
    public const string MeetingRoom = "567891234";
    public const string Storage = "678912345";
    public const string Accounts = "789123456";

    /// <summary>A fixed moment, so that a picture only changes when the app does.</summary>
    public static readonly DateTimeOffset Now = new(2026, 9, 28, 14, 32, 0, TimeSpan.Zero);

    /// <summary>
    /// 14:32 on a day relative to <see cref="Now"/>, in the local time zone: file listings and the device list show
    /// local time, and a moment given in UTC would read differently depending on where the pictures are made.
    /// </summary>
    public static DateTimeOffset LocalDay(int days)
    {
        var local = new DateTime(2026, 9, 28, 14, 32, 0, DateTimeKind.Local).AddDays(days);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

    public static DesktopConfig Config(string scratch) => new()
    {
        Language = "en",
        RendezvousServer = "rendezvous.example.com",
        FileTransferFolder = DocumentsFolder(scratch),
        Recent =
        [
            new RecentPeer(Reception, "Reception", Now.AddMinutes(-40)) { Platform = "Windows" },
            new RecentPeer(BuildServer, "build-server", Now.AddHours(-5)) { Platform = "Ubuntu 24.04" },
            new RecentPeer(AlicesMac, "Alice's MacBook", Now.AddDays(-2)) { Platform = "macOS" },
        ],
    };

    public static HostConfig HostConfig() => new()
    {
        DeviceName = "Studio PC",
        AllowlistEnabled = true,
        AllowedPeers = ["203.0.113.0/24", "id:" + Reception],
        TerminalEnabled = true,
    };

    /// <summary>A link to an engine that is not there, told what the engine would have said.</summary>
    public static HostLink Host()
    {
        var host = new HostLink("ui", NullLogger.Instance);
        host.Deliver(new IpcMessage { IdChanged = new IdChanged { Id = ThisDesk } });
        host.Deliver(new IpcMessage { ServerState = new ServerState { State = (int)RendezvousLinkState.Registered } });
        host.Deliver(new IpcMessage { ConfigSnapshot = new ConfigSnapshot { Json = HostConfig().ToJson() } });
        host.Deliver(new IpcMessage
        {
            PasswordState = new PasswordState
            {
                HasPermanent = true,
                TemporaryEnabled = true,
                TemporaryPassword = "w4tq8n",
                TemporaryLength = 6,
                LinkPassword = "sample",
            },
        });
        return host;
    }

    public static DeviceBook Devices() => new()
    {
        GroupNames = ["Office", "Servers", "Home"],
        Devices =
        [
            new SavedDevice { Target = Reception, Alias = "Reception", Group = "Office", Platform = "Windows", LastConnected = LocalDay(0).AddMinutes(-40) },
            new SavedDevice { Target = Accounts, Alias = "Accounts", Group = "Office", Platform = "Windows", Note = "Second floor" },
            new SavedDevice { Target = MeetingRoom, Alias = "Meeting room", Group = "Office", Platform = "Windows" },
            new SavedDevice { Target = BuildServer, Alias = "build-server", Group = "Servers", Platform = "Ubuntu 24.04", LastConnected = LocalDay(0).AddHours(-5) },
            new SavedDevice { Target = Storage, Alias = "storage", Group = "Servers", Platform = "Debian 12" },
            new SavedDevice { Target = AlicesMac, Alias = "Alice's MacBook", Group = "Home", Platform = "macOS", LastConnected = LocalDay(-2) },
        ],
    };

    public static Task<Dictionary<string, PeerOnlineState>> Presence(IReadOnlyList<string> targets, CancellationToken ct) =>
        Task.FromResult(targets.ToDictionary(
            t => t,
            t => t is MeetingRoom or Storage ? PeerOnlineState.Offline : PeerOnlineState.Online));

    public static IEnumerable<ConnectionHistoryEntry> History() =>
    [
        Entry(Reception, "Reception", "Windows", "203.0.113.24", false, "remote", "temporary", -40, -12,
            "PermKeyboard", "PermClipboard", "PermAudio"),
        Entry(AlicesMac, "Alice's MacBook", "macOS", "198.51.100.7", true, "file-transfer", "permanent", -190, -176, "PermFile"),
        Entry(BuildServer, "build-server", "Ubuntu 24.04", "203.0.113.51", false, "terminal", "permanent", -300, -281, "PermTerminal"),
        Entry(Accounts, "Accounts", "Windows", "203.0.113.30", false, "remote", "approval", -1460, -1402,
            "PermKeyboard", "PermClipboard"),
    ];

    private static ConnectionHistoryEntry Entry(
        string id, string name, string platform, string address, bool relayed, string kind, string auth,
        int startMinutes, int endMinutes, params string[] granted)
    {
        var entry = new ConnectionHistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedUtcMs = Now.AddMinutes(startMinutes).ToUnixTimeMilliseconds(),
            EndedUtcMs = Now.AddMinutes(endMinutes).ToUnixTimeMilliseconds(),
            PeerId = id,
            PeerName = name,
            PeerPlatform = platform,
            Address = address,
            AddressReported = relayed,
            Transport = relayed ? "relay" : "direct",
            Kind = kind,
            Authenticated = auth,
            Reason = "closed by the viewer",
            TerminalOpens = kind == "terminal" ? 1 : 0,
            TerminalIdentity = kind == "terminal" ? "alice" : string.Empty,
        };
        entry.Granted.Add(granted);
        return entry;
    }

    /// <summary>Some files for the file-transfer window's local side, dated so the listing never changes.</summary>
    private static string DocumentsFolder(string scratch)
    {
        string documents = Path.Combine(scratch, "Documents");
        Directory.CreateDirectory(Path.Combine(documents, "Invoices"));
        Directory.CreateDirectory(Path.Combine(documents, "Photos"));
        foreach ((string name, int kb) in new[] { ("Floor plan.pdf", 2360), ("Q3 figures.csv", 48), ("Contract draft.docx", 212), ("notes.txt", 3) })
        {
            string path = Path.Combine(documents, name);
            File.WriteAllBytes(path, new byte[kb * 1024]);
            File.SetLastWriteTime(path, LocalDay(-(name.Length % 5)).DateTime);
        }

        foreach (string dir in Directory.GetDirectories(documents))
        {
            Directory.SetLastWriteTime(dir, LocalDay(-7).DateTime);
        }

        return documents;
    }

    /// <summary>A shell on the build server, as the host's pty would send it: colours, a prompt, a few commands.</summary>
    public static byte[] TerminalOutput()
    {
        const string Prompt = "\u001b[1;32malice@build-server\u001b[0m:\u001b[1;34m~\u001b[0m$ ";
        var text = new StringBuilder();
        text.Append(Prompt).Append("uname -sr\r\n")
            .Append("Linux 6.8.0-45-generic\r\n")
            .Append(Prompt).Append("ls\r\n")
            .Append("\u001b[1;34mbackups\u001b[0m  \u001b[1;32mdeploy.sh\u001b[0m  \u001b[1;34mDocuments\u001b[0m  notes.md  \u001b[1;34mprojects\u001b[0m\r\n")
            .Append(Prompt).Append("df -h /\r\n")
            .Append("Filesystem      Size  Used Avail Use% Mounted on\r\n")
            .Append("/dev/sda2       234G   71G  151G  32% /\r\n")
            .Append(Prompt).Append("systemctl is-active nginx postgresql\r\n")
            .Append("\u001b[32mactive\u001b[0m\r\n")
            .Append("\u001b[32mactive\u001b[0m\r\n")
            .Append(Prompt).Append("uptime\r\n")
            .Append(" 14:32:07 up 12 days,  3:41,  1 user,  load average: 0.12, 0.09, 0.05\r\n")
            .Append(Prompt);
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
